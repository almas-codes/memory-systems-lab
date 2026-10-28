using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Strategies;

public sealed class MemoryMappedStoreStrategy : IStorageStrategy
{
    private ExtentStore? _store;
    private readonly StrategyStats _stats = new();

    public string Name => "MemoryMappedPageCache";
    public StrategyCapabilities Capabilities => StrategyCapabilities.FullPayload | StrategyCapabilities.FieldProjection | StrategyCapabilities.MemoryMapped | StrategyCapabilities.Persistence;

    public async ValueTask InitializeAsync(string dataDirectory, RecordManifest manifest, StrategyOptions options, CancellationToken ct = default)
    {
        _store = new ExtentStore(dataDirectory, manifest.RecordsPerSegment);
        await _store.InitializeAsync(openForReading: true, enableMMap: true, ct).ConfigureAwait(false);
    }

    public ValueTask<StrategyResult> GetAsync(ulong key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_store == null || !_store.TryLookupIndex(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return ValueTask.FromResult(StrategyResult.Miss);
        }

        byte[] buf = new byte[entry.Length];
        if (_store.TryReadMMap(entry.SegmentIndex, entry.Offset, entry.Length, buf))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.MemoryMappedReads);

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(buf.AsSpan(0, RecordHeader.HeaderSize));
            byte[] payload = buf.AsSpan(RecordHeader.HeaderSize, (int)header.PayloadLength).ToArray();
            return ValueTask.FromResult(StrategyResult.FromMMap(payload));
        }

        // Fallback to random access
        int read = _store.ReadRandom(entry.SegmentIndex, entry.Offset, buf);
        Interlocked.Increment(ref _stats.PhysicalReads);
        Interlocked.Add(ref _stats.PhysicalBytesRead, read);

        if (read < entry.Length)
        {
            Interlocked.Increment(ref _stats.Misses);
            return ValueTask.FromResult(StrategyResult.Miss);
        }

        RecordHeader h = MemoryMarshal.Read<RecordHeader>(buf.AsSpan(0, RecordHeader.HeaderSize));
        byte[] p = buf.AsSpan(RecordHeader.HeaderSize, (int)h.PayloadLength).ToArray();
        Interlocked.Increment(ref _stats.Hits);
        return ValueTask.FromResult(StrategyResult.FromExtent(p, read));
    }

    public ValueTask<StrategyResult> GetProjectionAsync(ulong key, int fieldIndex, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_store == null || !_store.TryLookupIndex(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return ValueTask.FromResult(StrategyResult.Miss);
        }

        Span<byte> hBuf = stackalloc byte[RecordHeader.HeaderSize];
        if (_store.TryReadMMap(entry.SegmentIndex, entry.Offset, RecordHeader.HeaderSize, hBuf))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.MemoryMappedReads);

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(hBuf);
            ulong val = fieldIndex == 0 ? header.Field1 : header.Field2;
            return ValueTask.FromResult(StrategyResult.FromMMap(ReadOnlyMemory<byte>.Empty));
        }

        int read = _store.ReadRandom(entry.SegmentIndex, entry.Offset, hBuf);
        Interlocked.Increment(ref _stats.PhysicalReads);
        Interlocked.Add(ref _stats.PhysicalBytesRead, read);

        RecordHeader h = MemoryMarshal.Read<RecordHeader>(hBuf);
        ulong v = fieldIndex == 0 ? h.Field1 : h.Field2;
        Interlocked.Increment(ref _stats.Hits);
        return ValueTask.FromResult(StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, read));
    }

    public async ValueTask<StrategyResult> ComputeDerivedAsync(ulong key, ulong param, CancellationToken ct = default)
    {
        var res = await GetProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        ulong derived = (res.ScalarValue * 31UL + param) ^ 0x5555555555555555UL;
        return StrategyResult.FromMMap(ReadOnlyMemory<byte>.Empty);
    }

    public async ValueTask<StrategyResult> EvaluatePredicateAsync(ulong key, ulong threshold, CancellationToken ct = default)
    {
        var res = await GetProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        return StrategyResult.FromMMap(ReadOnlyMemory<byte>.Empty);
    }

    public async ValueTask<bool> UpsertAsync(DataRecord record, CancellationToken ct = default)
    {
        if (_store == null) return false;
        var (seg, off, len) = await _store.AppendRecordAsync(record, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _stats.PhysicalWrites);
        Interlocked.Add(ref _stats.PhysicalBytesWritten, len);
        return true;
    }

    public ValueTask InvalidateAsync(ulong key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.Invalidations);
        return ValueTask.CompletedTask;
    }

    public StrategyStats GetStats() => _stats;
    public void ResetStats()
    {
        _stats.Hits = _stats.Misses = _stats.TotalRequests = _stats.PhysicalReads = _stats.PhysicalBytesRead = 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_store != null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }
}
