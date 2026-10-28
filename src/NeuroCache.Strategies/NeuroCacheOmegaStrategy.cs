using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Strategies;

public sealed class NeuroCacheOmegaStrategy : IStorageStrategy
{
    public sealed class StateWitness
    {
        public uint DomainId;
        public uint Generation = 1;
        public long LastModifiedTicks;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CompactAnswerEntry
    {
        public ulong Key;
        public uint WitnessDomainId;
        public uint WitnessGeneration;
        public ulong AnswerScalar;
        public ulong Seed1;
        public ulong Seed2;
        public long CapsuleOffset; // -1 if no capsule
        public const int Size = 48;
    }

    private const int DomainCount = 1024;
    private readonly StateWitness[] _witnesses = new StateWitness[DomainCount];
    private readonly ConcurrentDictionary<ulong, CompactAnswerEntry> _answerRegion = new();
    private ExtentStore? _store;
    private CapsuleStore? _capsuleStore;
    private StrategyOptions _options = new();
    private long _budgetLimit;
    private long _residentBytes;
    private readonly StrategyStats _stats = new();

    public string Name => "NeuroCacheOmega";
    public StrategyCapabilities Capabilities =>
        StrategyCapabilities.FullPayload |
        StrategyCapabilities.FieldProjection |
        StrategyCapabilities.PureRecomputation |
        StrategyCapabilities.SharedWitnesses |
        StrategyCapabilities.StorageCapsules |
        StrategyCapabilities.StrictRamBounded |
        StrategyCapabilities.Persistence;

    public NeuroCacheOmegaStrategy()
    {
        for (uint i = 0; i < DomainCount; i++)
        {
            _witnesses[i] = new StateWitness { DomainId = i, Generation = 1, LastModifiedTicks = DateTime.UtcNow.Ticks };
        }
    }

    public async ValueTask InitializeAsync(string dataDirectory, RecordManifest manifest, StrategyOptions options, CancellationToken ct = default)
    {
        _options = options;
        _budgetLimit = options.RamBudgetBytes;

        _store = new ExtentStore(dataDirectory, manifest.RecordsPerSegment);
        await _store.InitializeAsync(openForReading: true, enableMMap: false, ct).ConfigureAwait(false);

        if (_options.EnableCapsules)
        {
            _capsuleStore = new CapsuleStore(dataDirectory);
            await _capsuleStore.InitializeAsync(ct).ConfigureAwait(false);
        }
    }

    private uint GetDomain(ulong key) => (uint)(key % (ulong)DomainCount);

    private bool IsWitnessValid(in CompactAnswerEntry entry)
    {
        if (!_options.EnableSharedWitnesses)
            return true;

        var witness = _witnesses[entry.WitnessDomainId];
        return witness.Generation == entry.WitnessGeneration;
    }

    public async ValueTask<StrategyResult> GetAsync(ulong key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        // Check if capsule exists on disk and is valid
        if (_options.EnableCapsules && _capsuleStore != null && _answerRegion.TryGetValue(key, out var entry) && entry.CapsuleOffset >= 0)
        {
            if (IsWitnessValid(in entry))
            {
                byte[] capBuf = ArrayPool<byte>.Shared.Rent(256);
                try
                {
                    if (_capsuleStore.TryReadCapsule(entry.CapsuleOffset, capBuf, out var header, out int totalBytesRead))
                    {
                        if (header.WitnessGeneration == _witnesses[entry.WitnessDomainId].Generation)
                        {
                            Interlocked.Increment(ref _stats.Hits);
                            Interlocked.Increment(ref _stats.CapsuleReads);
                            Interlocked.Increment(ref _stats.PhysicalReads);
                            Interlocked.Add(ref _stats.PhysicalBytesRead, totalBytesRead);
                            byte[] payload = capBuf.AsSpan(0, (int)header.ResultLength).ToArray();
                            return StrategyResult.FromCapsule(payload, totalBytesRead);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(capBuf);
                }
            }
        }

        // Full payload read from extent store
        if (_store == null || !_store.TryLookupIndex(key, out var indexEntry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return StrategyResult.Miss;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(indexEntry.Length);
        try
        {
            int read = await _store.ReadRandomAsync(indexEntry.SegmentIndex, indexEntry.Offset, buf.AsMemory(0, indexEntry.Length), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalReads);
            Interlocked.Add(ref _stats.PhysicalBytesRead, read);

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(buf.AsSpan(0, RecordHeader.HeaderSize));
            byte[] payload = buf.AsSpan(RecordHeader.HeaderSize, (int)header.PayloadLength).ToArray();

            // Register answer region entry under budget
            uint domain = GetDomain(key);
            int entrySize = CompactAnswerEntry.Size + 32;
            if (Interlocked.Read(ref _residentBytes) + entrySize <= _budgetLimit)
            {
                var newEntry = new CompactAnswerEntry
                {
                    Key = key,
                    WitnessDomainId = domain,
                    WitnessGeneration = _witnesses[domain].Generation,
                    AnswerScalar = header.Field1,
                    Seed1 = header.Field1,
                    Seed2 = header.Field2,
                    CapsuleOffset = -1
                };

                if (_answerRegion.TryAdd(key, newEntry))
                {
                    Interlocked.Add(ref _residentBytes, entrySize);
                }
            }

            Interlocked.Increment(ref _stats.Hits);
            return StrategyResult.FromExtent(payload, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public async ValueTask<StrategyResult> GetProjectionAsync(ulong key, int fieldIndex, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_answerRegion.TryGetValue(key, out var entry))
        {
            if (IsWitnessValid(in entry))
            {
                Interlocked.Increment(ref _stats.Hits);
                Interlocked.Increment(ref _stats.InlineReuses);
                ulong val = fieldIndex == 0 ? entry.Seed1 : entry.Seed2;
                return StrategyResult.FromInline(val);
            }
            else
            {
                // Stale witness: remove stale entry
                _answerRegion.TryRemove(key, out _);
                Interlocked.Add(ref _residentBytes, -(CompactAnswerEntry.Size + 32));
                Interlocked.Increment(ref _stats.Invalidations);
            }
        }

        // Miss/stale: fetch header and populate
        return await DemandFetchProjectionAsync(key, fieldIndex, ct).ConfigureAwait(false);
    }

    private async ValueTask<StrategyResult> DemandFetchProjectionAsync(ulong key, int fieldIndex, CancellationToken ct)
    {
        if (_store == null || !_store.TryLookupIndex(key, out var indexEntry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return StrategyResult.Miss;
        }

        byte[] hBuf = ArrayPool<byte>.Shared.Rent(RecordHeader.HeaderSize);
        try
        {
            int read = await _store.ReadRandomAsync(indexEntry.SegmentIndex, indexEntry.Offset, hBuf.AsMemory(0, RecordHeader.HeaderSize), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalReads);
            Interlocked.Add(ref _stats.PhysicalBytesRead, read);

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(hBuf.AsSpan(0, RecordHeader.HeaderSize));
            uint domain = GetDomain(key);

            long capOffset = -1;
            if (_options.EnableCapsules && _capsuleStore != null)
            {
                // Materialize single-read capsule on disk asynchronously
                capOffset = await _capsuleStore.WriteCapsuleAsync(
                    key,
                    1,
                    domain,
                    _witnesses[domain].Generation,
                    header.Field1,
                    header.Field2,
                    header.Checksum,
                    ReadOnlyMemory<byte>.Empty,
                    ct).ConfigureAwait(false);
            }

            int entrySize = CompactAnswerEntry.Size + 32;
            if (Interlocked.Read(ref _residentBytes) + entrySize <= _budgetLimit)
            {
                var newEntry = new CompactAnswerEntry
                {
                    Key = key,
                    WitnessDomainId = domain,
                    WitnessGeneration = _witnesses[domain].Generation,
                    AnswerScalar = header.Field1,
                    Seed1 = header.Field1,
                    Seed2 = header.Field2,
                    CapsuleOffset = capOffset
                };

                if (_answerRegion.TryAdd(key, newEntry))
                {
                    Interlocked.Add(ref _residentBytes, entrySize);
                }
            }

            Interlocked.Increment(ref _stats.Hits);
            ulong val = fieldIndex == 0 ? header.Field1 : header.Field2;
            return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hBuf);
        }
    }

    public async ValueTask<StrategyResult> ComputeDerivedAsync(ulong key, ulong param, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_options.EnableRecomputation && _answerRegion.TryGetValue(key, out var entry))
        {
            if (IsWitnessValid(in entry))
            {
                Interlocked.Increment(ref _stats.Hits);
                Interlocked.Increment(ref _stats.Recomputes);
                ulong derived = (entry.Seed1 * 31UL + param) ^ 0x5555555555555555UL;
                return StrategyResult.FromRecompute(derived);
            }
        }

        var res = await DemandFetchProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        ulong d = (res.ScalarValue * 31UL + param) ^ 0x5555555555555555UL;
        return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, res.BytesRead);
    }

    public async ValueTask<StrategyResult> EvaluatePredicateAsync(ulong key, ulong threshold, CancellationToken ct = default)
    {
        if (_options.EnableRecomputation && _answerRegion.TryGetValue(key, out var entry))
        {
            if (IsWitnessValid(in entry))
            {
                Interlocked.Increment(ref _stats.Hits);
                Interlocked.Increment(ref _stats.Recomputes);
                return StrategyResult.FromRecompute(entry.Seed1 > threshold ? 1UL : 0UL);
            }
        }

        var res = await DemandFetchProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, res.BytesRead);
    }

    public async ValueTask<bool> UpsertAsync(DataRecord record, CancellationToken ct = default)
    {
        if (_store == null) return false;
        var (seg, off, len) = await _store.AppendRecordAsync(record, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _stats.PhysicalWrites);
        Interlocked.Add(ref _stats.PhysicalBytesWritten, len);

        uint domain = GetDomain(record.Key);

        // Advance State Witness generation! Invalidates all stale derived claims in this domain in O(1)!
        Interlocked.Increment(ref _witnesses[domain].Generation);
        _witnesses[domain].LastModifiedTicks = DateTime.UtcNow.Ticks;

        // Update answer region
        var entry = new CompactAnswerEntry
        {
            Key = record.Key,
            WitnessDomainId = domain,
            WitnessGeneration = _witnesses[domain].Generation,
            AnswerScalar = record.Field1,
            Seed1 = record.Field1,
            Seed2 = record.Field2,
            CapsuleOffset = -1
        };

        _answerRegion[record.Key] = entry;
        return true;
    }

    public ValueTask InvalidateAsync(ulong key, CancellationToken ct = default)
    {
        uint domain = GetDomain(key);
        // Advance State Witness generation to invalidate domain
        Interlocked.Increment(ref _witnesses[domain].Generation);
        _answerRegion.TryRemove(key, out _);
        Interlocked.Increment(ref _stats.Invalidations);
        return ValueTask.CompletedTask;
    }

    public StrategyStats GetStats()
    {
        _stats.ResidentBytes = _residentBytes;
        return _stats;
    }

    public void ResetStats()
    {
        _stats.Hits = _stats.Misses = _stats.TotalRequests = _stats.PhysicalReads = _stats.PhysicalBytesRead = 0;
    }

    public async ValueTask DisposeAsync()
    {
        _answerRegion.Clear();
        if (_capsuleStore != null)
        {
            await _capsuleStore.DisposeAsync().ConfigureAwait(false);
            _capsuleStore = null;
        }
        if (_store != null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }
}
