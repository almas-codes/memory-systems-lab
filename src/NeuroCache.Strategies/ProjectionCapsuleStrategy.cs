using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Strategies;

public sealed class ProjectionCapsuleStrategy : IStorageStrategy
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CapsulePointer
    {
        public ulong Key;
        public long CapsuleOffset;
        public uint Revision;

        public const int Size = 20;
    }

    private readonly ConcurrentDictionary<ulong, CapsulePointer> _capsuleIndex = new();
    private ExtentStore? _extentStore;
    private CapsuleStore? _capsuleStore;
    private long _budgetLimit;
    private long _residentBytes;
    private readonly StrategyStats _stats = new();

    public string Name => "ProjectionCapsules";
    public StrategyCapabilities Capabilities => StrategyCapabilities.FieldProjection | StrategyCapabilities.StorageCapsules | StrategyCapabilities.StrictRamBounded | StrategyCapabilities.Persistence;

    public async ValueTask InitializeAsync(string dataDirectory, RecordManifest manifest, StrategyOptions options, CancellationToken ct = default)
    {
        _budgetLimit = options.RamBudgetBytes;

        _extentStore = new ExtentStore(dataDirectory, manifest.RecordsPerSegment);
        await _extentStore.InitializeAsync(openForReading: true, enableMMap: false, ct).ConfigureAwait(false);

        _capsuleStore = new CapsuleStore(dataDirectory);
        await _capsuleStore.InitializeAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<StrategyResult> GetAsync(ulong key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_extentStore == null || !_extentStore.TryLookupIndex(key, out var indexEntry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return StrategyResult.Miss;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(indexEntry.Length);
        try
        {
            int read = await _extentStore.ReadRandomAsync(indexEntry.SegmentIndex, indexEntry.Offset, buf.AsMemory(0, indexEntry.Length), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalReads);
            Interlocked.Add(ref _stats.PhysicalBytesRead, read);

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(buf.AsSpan(0, RecordHeader.HeaderSize));
            byte[] payload = buf.AsSpan(RecordHeader.HeaderSize, (int)header.PayloadLength).ToArray();

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

        // Check if capsule exists on disk: exactly ONE storage read!
        if (_capsuleStore != null && _capsuleIndex.TryGetValue(key, out var ptr) && ptr.CapsuleOffset >= 0)
        {
            byte[] capBuf = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                if (_capsuleStore.TryReadCapsule(ptr.CapsuleOffset, capBuf, out var header, out int totalBytesRead))
                {
                    Interlocked.Increment(ref _stats.PhysicalReads);
                    Interlocked.Add(ref _stats.PhysicalBytesRead, totalBytesRead);

                    if (header.Key == key && header.OperationRev == ptr.Revision)
                    {
                        Interlocked.Increment(ref _stats.Hits);
                        Interlocked.Increment(ref _stats.CapsuleReads);
                        ulong val = fieldIndex == 0 ? header.ProjectedField1 : header.ProjectedField2;
                        return StrategyResult.FromCapsule(val, totalBytesRead);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(capBuf);
            }
        }

        // Capsule miss: fallback to extent store, then write capsule
        Interlocked.Increment(ref _stats.Fallbacks);
        if (_extentStore == null || !_extentStore.TryLookupIndex(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return StrategyResult.Miss;
        }

        byte[] hBuf = ArrayPool<byte>.Shared.Rent(RecordHeader.HeaderSize);
        try
        {
            int read = await _extentStore.ReadRandomAsync(entry.SegmentIndex, entry.Offset, hBuf.AsMemory(0, RecordHeader.HeaderSize), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalReads);
            Interlocked.Add(ref _stats.PhysicalBytesRead, read);

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(hBuf.AsSpan(0, RecordHeader.HeaderSize));

            if (_capsuleStore != null)
            {
                long offset = await _capsuleStore.WriteCapsuleAsync(
                    key,
                    header.Version,
                    header.TenantId,
                    1,
                    header.Field1,
                    header.Field2,
                    header.Checksum,
                    ReadOnlyMemory<byte>.Empty,
                    ct).ConfigureAwait(false);

                int ptrSize = CapsulePointer.Size + 32;
                if (Interlocked.Read(ref _residentBytes) + ptrSize <= _budgetLimit)
                {
                    var newPtr = new CapsulePointer
                    {
                        Key = key,
                        CapsuleOffset = offset,
                        Revision = header.Version
                    };

                    if (_capsuleIndex.TryAdd(key, newPtr))
                    {
                        Interlocked.Add(ref _residentBytes, ptrSize);
                    }
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
        var res = await GetProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        ulong derived = (res.ScalarValue * 31UL + param) ^ 0x5555555555555555UL;
        return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, res.BytesRead);
    }

    public async ValueTask<StrategyResult> EvaluatePredicateAsync(ulong key, ulong threshold, CancellationToken ct = default)
    {
        var res = await GetProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, res.BytesRead);
    }

    public async ValueTask<bool> UpsertAsync(DataRecord record, CancellationToken ct = default)
    {
        if (_extentStore == null) return false;
        var (seg, off, len) = await _extentStore.AppendRecordAsync(record, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _stats.PhysicalWrites);
        Interlocked.Add(ref _stats.PhysicalBytesWritten, len);

        if (_capsuleStore != null)
        {
            long capOffset = await _capsuleStore.WriteCapsuleAsync(
                record.Key,
                record.Version,
                record.TenantId,
                1,
                record.Field1,
                record.Field2,
                record.CalculateChecksum(),
                ReadOnlyMemory<byte>.Empty,
                ct).ConfigureAwait(false);

            _capsuleIndex[record.Key] = new CapsulePointer
            {
                Key = record.Key,
                CapsuleOffset = capOffset,
                Revision = record.Version
            };
        }

        return true;
    }

    public ValueTask InvalidateAsync(ulong key, CancellationToken ct = default)
    {
        if (_capsuleIndex.TryRemove(key, out _))
        {
            Interlocked.Add(ref _residentBytes, -(CapsulePointer.Size + 32));
            Interlocked.Increment(ref _stats.Invalidations);
        }
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
        _capsuleIndex.Clear();
        if (_capsuleStore != null)
        {
            await _capsuleStore.DisposeAsync().ConfigureAwait(false);
            _capsuleStore = null;
        }
        if (_extentStore != null)
        {
            await _extentStore.DisposeAsync().ConfigureAwait(false);
            _extentStore = null;
        }
    }
}
