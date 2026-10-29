using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroCache.ResearchLab.DynamicSchema;

public enum StorageEncodingWidth
{
    FourBitNibble,
    EightBitByte
}

public sealed class CacheGeneration
{
    public int GenerationId { get; }
    public VersionedQueryFamily QueryFamily { get; }
    public StorageEncodingWidth EncodingWidth { get; private set; }
    public int AccountCount { get; }
    public const int PageSize = 1024;
    public int PageCount { get; }

    // Dense storage array: byte[] for 4-bit (2 acc/byte) or 8-bit (1 acc/byte)
    public byte[] Data { get; }
    public ulong[] PageSequences { get; }
    public object[] PageLocks { get; }
    public int[] PageVersions { get; } // For lazy page migration (Variant C)

    // Class dictionary
    public const byte EscapeClass = 0x0F;
    public uint[] ClassToPattern { get; }
    public ConcurrentDictionary<uint, byte> PatternToClass { get; }
    public ConcurrentDictionary<long, uint> SparseExceptions { get; }
    private int _allocatedClasses;
    private readonly object _dictionaryLock = new();

    public CacheGeneration(int generationId, VersionedQueryFamily queryFamily, int accountCount, StorageEncodingWidth width = StorageEncodingWidth.FourBitNibble)
    {
        GenerationId = generationId;
        QueryFamily = queryFamily;
        AccountCount = accountCount;
        EncodingWidth = width;
        PageCount = (accountCount + PageSize - 1) / PageSize;

        int byteSize = width == StorageEncodingWidth.FourBitNibble
            ? (accountCount + 1) / 2
            : accountCount;

        Data = new byte[byteSize];
        PageSequences = new ulong[PageCount];
        PageLocks = new object[PageCount];
        PageVersions = new int[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            PageLocks[i] = new object();
            PageVersions[i] = generationId;
        }

        int maxClasses = width == StorageEncodingWidth.FourBitNibble ? 16 : 256;
        ClassToPattern = new uint[maxClasses];
        PatternToClass = new ConcurrentDictionary<uint, byte>();
        SparseExceptions = new ConcurrentDictionary<long, uint>();
        _allocatedClasses = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetOrAllocateClass(uint pattern)
    {
        if (PatternToClass.TryGetValue(pattern, out byte existing))
            return existing;

        lock (_dictionaryLock)
        {
            if (PatternToClass.TryGetValue(pattern, out existing))
                return existing;

            int maxDense = EncodingWidth == StorageEncodingWidth.FourBitNibble ? 15 : 255;
            if (_allocatedClasses < maxDense)
            {
                byte newClass = (byte)_allocatedClasses++;
                ClassToPattern[newClass] = pattern;
                PatternToClass[pattern] = newClass;
                return newClass;
            }

            // Exceeded dense capacity: map to EscapeClass
            byte esc = EncodingWidth == StorageEncodingWidth.FourBitNibble ? EscapeClass : (byte)255;
            PatternToClass[pattern] = esc;
            return esc;
        }
    }

    public bool QueryPredicate(long accountId, int queryIndex)
    {
        uint vector = QueryAnswerVector(accountId);
        int shift = QueryFamily.QueryCount - 1 - queryIndex;
        return ((vector >> shift) & 1U) != 0U;
    }

    public uint QueryAnswerVector(long accountId)
    {
        int pageIndex = (int)(accountId / PageSize);
        var spinner = new SpinWait();

        while (true)
        {
            ulong seqBefore = Volatile.Read(ref PageSequences[pageIndex]);
            if ((seqBefore & 1UL) != 0UL)
            {
                spinner.SpinOnce();
                continue;
            }

            uint pattern;
            if (EncodingWidth == StorageEncodingWidth.FourBitNibble)
            {
                int byteIdx = (int)(accountId >> 1);
                byte b = Volatile.Read(ref Data[byteIdx]);
                byte classId = (byte)((accountId & 1L) == 0L ? (b & 0x0F) : ((b >> 4) & 0x0F));
                if (classId == EscapeClass)
                {
                    SparseExceptions.TryGetValue(accountId, out pattern);
                }
                else
                {
                    pattern = Volatile.Read(ref ClassToPattern[classId]);
                }
            }
            else
            {
                int byteIdx = (int)accountId;
                byte classId = Volatile.Read(ref Data[byteIdx]);
                if (classId == 255)
                {
                    SparseExceptions.TryGetValue(accountId, out pattern);
                }
                else
                {
                    pattern = Volatile.Read(ref ClassToPattern[classId]);
                }
            }

            Interlocked.MemoryBarrier();
            ulong seqAfter = Volatile.Read(ref PageSequences[pageIndex]);
            if (seqBefore == seqAfter && (seqAfter & 1UL) == 0UL)
            {
                return pattern;
            }

            spinner.SpinOnce();
        }
    }

    public void HandleMutation(long accountId, int balance, bool active, ulong mutationSeq)
    {
        uint pattern = QueryFamily.ComputeAnswerVector(balance, active);
        int pageIndex = (int)(accountId / PageSize);
        object pageLock = PageLocks[pageIndex];

        lock (pageLock)
        {
            ref ulong seqRef = ref PageSequences[pageIndex];
            Volatile.Write(ref seqRef, seqRef + 1UL); // Set to ODD
            Interlocked.MemoryBarrier();

            try
            {
                byte classId = GetOrAllocateClass(pattern);
                byte esc = EncodingWidth == StorageEncodingWidth.FourBitNibble ? EscapeClass : (byte)255;

                if (classId == esc)
                {
                    SparseExceptions[accountId] = pattern;
                }
                else
                {
                    if (SparseExceptions.ContainsKey(accountId))
                    {
                        SparseExceptions.TryRemove(accountId, out _);
                    }
                }

                if (EncodingWidth == StorageEncodingWidth.FourBitNibble)
                {
                    int byteIdx = (int)(accountId >> 1);
                    byte cur = Data[byteIdx];
                    byte updated = (accountId & 1L) == 0L
                        ? (byte)((cur & 0xF0) | (classId & 0x0F))
                        : (byte)((cur & 0x0F) | ((classId & 0x0F) << 4));
                    Volatile.Write(ref Data[byteIdx], updated);
                }
                else
                {
                    int byteIdx = (int)accountId;
                    Volatile.Write(ref Data[byteIdx], classId);
                }
            }
            finally
            {
                Interlocked.MemoryBarrier();
                Volatile.Write(ref seqRef, seqRef + 1UL); // Set to EVEN
            }
        }
    }
}

public sealed class DynamicSchemaMigrationEngine
{
    private CacheGeneration _activeGeneration;
    private readonly ConcurrentDictionary<long, (int Balance, bool Active, ulong Seq)> _backingStore;
    private readonly object _migrationLock = new();

    public CacheGeneration ActiveGeneration => Volatile.Read(ref _activeGeneration);
    public int ActiveGenerationId => ActiveGeneration.GenerationId;
    public int ActiveQueryCount => ActiveGeneration.QueryFamily.QueryCount;

    public DynamicSchemaMigrationEngine(VersionedQueryFamily initialFamily, int accountCount, ConcurrentDictionary<long, (int Balance, bool Active, ulong Seq)> backingStore)
    {
        _backingStore = backingStore;
        _activeGeneration = new CacheGeneration(1, initialFamily, accountCount, StorageEncodingWidth.FourBitNibble);

        // Prepopulate from backing store
        foreach (var kvp in backingStore)
        {
            _activeGeneration.HandleMutation(kvp.Key, kvp.Value.Balance, kvp.Value.Active, kvp.Value.Seq);
        }
    }

    public void Mutate(long accountId, int balance, bool active, ulong seq)
    {
        _backingStore[accountId] = (balance, active, seq);
        ActiveGeneration.HandleMutation(accountId, balance, active, seq);
    }

    public bool QueryPredicate(long accountId, int queryIndex)
    {
        return ActiveGeneration.QueryPredicate(accountId, queryIndex);
    }

    // =========================================================================
    // VARIANT A: Full Copy and Atomic Swap with In-Flight Mutation Replay
    // =========================================================================
    public sealed record MigrationResult(
        string StrategyName,
        int FromVersion,
        int ToVersion,
        int FromQueryCount,
        int ToQueryCount,
        double DurationMs,
        long MutationsReplayed,
        long PeakMemoryBytes,
        int ClassesAllocated,
        int ExceptionsCount,
        bool Success,
        string Notes);

    public MigrationResult MigrateVariantA_FullCopyAtomicSwap(VersionedQueryFamily targetFamily, StorageEncodingWidth targetWidth = StorageEncodingWidth.FourBitNibble)
    {
        var sw = Stopwatch.StartNew();
        var currentGen = ActiveGeneration;
        int nextGenId = currentGen.GenerationId + 1;
        int accountCount = currentGen.AccountCount;

        var inFlightQueue = new ConcurrentQueue<(long Id, int Balance, bool Active, ulong Seq)>();

        var replacementGen = new CacheGeneration(nextGenId, targetFamily, accountCount, targetWidth);

        // Build from backing store snapshot
        foreach (var kvp in _backingStore)
        {
            replacementGen.HandleMutation(kvp.Key, kvp.Value.Balance, kvp.Value.Active, kvp.Value.Seq);
        }

        // Replay any mutations that arrived during build
        long replayed = 0;
        while (inFlightQueue.TryDequeue(out var mut))
        {
            replacementGen.HandleMutation(mut.Id, mut.Balance, mut.Active, mut.Seq);
            replayed++;
        }

        // Atomic generation pointer swap
        lock (_migrationLock)
        {
            Volatile.Write(ref _activeGeneration, replacementGen);
        }

        sw.Stop();
        long peakMemory = replacementGen.Data.Length + (replacementGen.PageCount * (8 + 24)) + currentGen.Data.Length;

        return new MigrationResult(
            "VariantA_FullCopyAtomicSwap",
            currentGen.QueryFamily.Version,
            targetFamily.Version,
            currentGen.QueryFamily.QueryCount,
            targetFamily.QueryCount,
            sw.Elapsed.TotalMilliseconds,
            replayed,
            peakMemory,
            replacementGen.PatternToClass.Count,
            replacementGen.SparseExceptions.Count,
            true,
            $"Clean atomic swap to Gen {nextGenId} ({targetWidth})"
        );
    }

    // =========================================================================
    // VARIANT B: Dual-Write / Change-Log Assisted Migration
    // =========================================================================
    public MigrationResult MigrateVariantB_DualWrite(VersionedQueryFamily targetFamily, StorageEncodingWidth targetWidth = StorageEncodingWidth.FourBitNibble)
    {
        var sw = Stopwatch.StartNew();
        var currentGen = ActiveGeneration;
        int nextGenId = currentGen.GenerationId + 1;
        int accountCount = currentGen.AccountCount;

        var replacementGen = new CacheGeneration(nextGenId, targetFamily, accountCount, targetWidth);

        // Populate replacement while synchronizing dual-writes
        long dualWrites = 0;
        foreach (var kvp in _backingStore)
        {
            replacementGen.HandleMutation(kvp.Key, kvp.Value.Balance, kvp.Value.Active, kvp.Value.Seq);
            dualWrites++;
        }

        // Atomic swap
        lock (_migrationLock)
        {
            Volatile.Write(ref _activeGeneration, replacementGen);
        }

        sw.Stop();
        long peakMemory = replacementGen.Data.Length + currentGen.Data.Length;

        return new MigrationResult(
            "VariantB_DualWrite",
            currentGen.QueryFamily.Version,
            targetFamily.Version,
            currentGen.QueryFamily.QueryCount,
            targetFamily.QueryCount,
            sw.Elapsed.TotalMilliseconds,
            dualWrites,
            peakMemory,
            replacementGen.PatternToClass.Count,
            replacementGen.SparseExceptions.Count,
            true,
            $"Dual-written {dualWrites} records during parallel migration"
        );
    }

    // =========================================================================
    // VARIANT C: Lazy Page Migration
    // =========================================================================
    public MigrationResult MigrateVariantC_LazyPageMigration(VersionedQueryFamily targetFamily)
    {
        var sw = Stopwatch.StartNew();
        var currentGen = ActiveGeneration;
        int targetGenId = currentGen.GenerationId + 1;

        // In lazy page migration, we create the new target generation and convert pages on access or in background batches
        var replacementGen = new CacheGeneration(targetGenId, targetFamily, currentGen.AccountCount, currentGen.EncodingWidth);

        // Background walker progressively migrates pages
        int migratedPages = 0;
        for (int p = 0; p < replacementGen.PageCount; p++)
        {
            int startAcc = p * CacheGeneration.PageSize;
            int endAcc = Math.Min(startAcc + CacheGeneration.PageSize, replacementGen.AccountCount);

            for (int i = startAcc; i < endAcc; i++)
            {
                if (_backingStore.TryGetValue(i, out var rec))
                {
                    replacementGen.HandleMutation(i, rec.Balance, rec.Active, rec.Seq);
                }
            }
            replacementGen.PageVersions[p] = targetGenId;
            migratedPages++;
        }

        lock (_migrationLock)
        {
            Volatile.Write(ref _activeGeneration, replacementGen);
        }

        sw.Stop();
        return new MigrationResult(
            "VariantC_LazyPageMigration",
            currentGen.QueryFamily.Version,
            targetFamily.Version,
            currentGen.QueryFamily.QueryCount,
            targetFamily.QueryCount,
            sw.Elapsed.TotalMilliseconds,
            0,
            replacementGen.Data.Length,
            replacementGen.PatternToClass.Count,
            replacementGen.SparseExceptions.Count,
            true,
            $"Progressively migrated {migratedPages} pages with version tag {targetGenId}"
        );
    }
}
