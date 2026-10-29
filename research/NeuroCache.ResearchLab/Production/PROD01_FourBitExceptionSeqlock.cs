using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.EapResearch.AdaptiveCompression;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.Production;

/// <summary>
/// PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001:
/// Full integrated production candidate combining:
/// 1. SEM-CLASS4-003: 4-bit packed dense nibble array (0.50 bytes/record).
/// 2. COMBO-SEM-SPARSE-002: 16-slot class dictionary with slot 0x0F escaping to a concurrent sparse dictionary for rare exceptions.
/// 3. REC-SEQLOCK-003: 1024-account pages protected by 64-bit seqlock sequence counters and page-level writer locks.
/// </summary>
public sealed class PROD01_FourBitExceptionSeqlock : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public const byte EscapeClass = 0x0F;
    public const int PageSize = 1024;
    public const int PageShift = 10;
    public const int PageMask = PageSize - 1;

    public string ExperimentId => "PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001";
    public string CandidateName => "Integrated 4-Bit Semantic Classes with Sparse Exceptions and Seqlock Pages";
    public string Family => "ProductionCandidate";
    public string Description => "End-to-end integration of 4-bit packed nibbles, 16-slot class dictionary, 0x0F rare exception escape table, and 1024-account page seqlocks.";

    private readonly long _maxId;
    private readonly int _pageCount;
    private readonly byte[] _data;
    private readonly byte[] _classToPattern = new byte[16];
    private readonly byte[] _patternToClass = new byte[256];
    private readonly ConcurrentDictionary<long, byte> _sparseExceptions;
    private readonly long[] _pageSequences;
    private readonly object[] _pageLocks;
    private readonly object _dictLock = new();
    private int _distinctClasses;
    private long _mutationCount;

    public long MaxId => _maxId;
    public int PageCount => _pageCount;
    public int DistinctClassCount => Volatile.Read(ref _distinctClasses);
    public int SparseExceptionCount => _sparseExceptions.Count;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    // Memory Accounting: Detailed breakdown
    public long PayloadBytes => _data.LongLength;
    public long MetadataBytes =>
        _classToPattern.Length +
        _patternToClass.Length +
        (long)_pageCount * sizeof(long) + // _pageSequences
        (long)_pageCount * (IntPtr.Size + 24) + // _pageLocks references + object headers
        128L + (long)_sparseExceptions.Count * 32L; // ConcurrentDictionary overhead

    public long TotalAllocatedBytes => PayloadBytes + MetadataBytes;

    public PROD01_FourBitExceptionSeqlock(long maxId)
    {
        _maxId = maxId;
        _pageCount = (int)((maxId + PageSize) / PageSize);
        _data = new byte[(maxId + 2) / 2];
        _sparseExceptions = new ConcurrentDictionary<long, byte>();
        _pageSequences = new long[_pageCount];
        _pageLocks = new object[_pageCount];

        for (int p = 0; p < _pageCount; p++)
        {
            _pageLocks[p] = new object();
        }

        Array.Fill(_patternToClass, (byte)0xFF);

        // Pre-register the 8 empirical production patterns into slots 0..7
        byte[] productionPatterns = { 128, 193, 0, 1, 251, 243, 255, 3 };
        foreach (byte p in productionPatterns)
        {
            RegisterInitialPattern(p);
        }
    }

    private void RegisterInitialPattern(byte pattern)
    {
        if (_distinctClasses >= 15) throw new InvalidOperationException("Initial classes exceed 15.");
        byte classId = (byte)_distinctClasses;
        _classToPattern[classId] = pattern;
        _patternToClass[pattern] = classId;
        _distinctClasses++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetOrAssignClass(byte pattern)
    {
        byte existing = Volatile.Read(ref _patternToClass[pattern]);
        if (existing != 0xFF) return existing;

        lock (_dictLock)
        {
            if (_patternToClass[pattern] != 0xFF) return _patternToClass[pattern];
            if (_distinctClasses < 15) // Slots 0..14 reserved for dense classes
            {
                byte classId = (byte)_distinctClasses;
                _classToPattern[classId] = pattern;
                _patternToClass[pattern] = classId;
                _distinctClasses++;
                return classId;
            }

            // Slot 15 (0x0F) is the escape class
            _patternToClass[pattern] = EscapeClass;
            return EscapeClass;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id) => QueryAll(id);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        if (id < 0 || id > _maxId) return 0;
        int pageIndex = (int)(id >> PageShift);

        var spinner = new SpinWait();
        while (true)
        {
            long seqBefore = Volatile.Read(ref _pageSequences[pageIndex]);
            if ((seqBefore & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            long byteIdx = id >> 1;
            byte pair = Volatile.Read(ref _data[byteIdx]);
            byte cls = (id & 1) == 0 ? (byte)(pair & 0x0F) : (byte)(pair >> 4);

            byte pattern;
            if (cls != EscapeClass)
            {
                pattern = _classToPattern[cls];
            }
            else
            {
                pattern = _sparseExceptions.TryGetValue(id, out byte exc) ? exc : (byte)0;
            }

            long seqAfter = Volatile.Read(ref _pageSequences[pageIndex]);
            if (seqBefore == seqAfter)
            {
                return pattern;
            }

            spinner.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        byte pattern = QueryAll(id);
        return ((pattern >> (7 - predicateIndex)) & 1) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        byte pattern = QueryFamily.ComputeAnswerPattern(balance, active);
        HandleMutationPattern(id, pattern, sequenceId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutationPattern(long id, byte pattern, long sequenceId)
    {
        if (id < 0 || id > _maxId) return;
        int pageIndex = (int)(id >> PageShift);

        lock (_pageLocks[pageIndex])
        {
            ref long pageSeq = ref _pageSequences[pageIndex];
            long curSeq = Volatile.Read(ref pageSeq);
            Volatile.Write(ref pageSeq, curSeq + 1); // Set to ODD (write in progress)

            try
            {
                byte cls = GetOrAssignClass(pattern);

                long byteIdx = id >> 1;
                bool isLow = (id & 1) == 0;

                if (cls != EscapeClass)
                {
                    ref byte target = ref _data[byteIdx];
                    byte curByte = Volatile.Read(ref target);
                    byte nextByte = isLow
                        ? (byte)((curByte & 0xF0) | (cls & 0x0F))
                        : (byte)((curByte & 0x0F) | (cls << 4));
                    Volatile.Write(ref target, nextByte);

                    // Evict from sparse dictionary if returning to normal class
                    if (_sparseExceptions.ContainsKey(id))
                    {
                        _sparseExceptions.TryRemove(id, out _);
                    }
                }
                else
                {
                    ref byte target = ref _data[byteIdx];
                    byte curByte = Volatile.Read(ref target);
                    byte nextByte = isLow
                        ? (byte)((curByte & 0xF0) | 0x0F)
                        : (byte)((curByte & 0x0F) | 0xF0);
                    Volatile.Write(ref target, nextByte);

                    _sparseExceptions[id] = pattern;
                }

                Interlocked.Increment(ref _mutationCount);
            }
            finally
            {
                long finalSeq = Volatile.Read(ref pageSeq);
                Volatile.Write(ref pageSeq, finalSeq + 1); // Set to EVEN (write complete)
            }
        }
    }
}
