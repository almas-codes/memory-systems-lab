using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Strategies;

public sealed class DirectBaselineStrategy : IStorageStrategy
{
    private ExtentStore? _store;
    private RecordManifest? _manifest;
    private readonly StrategyStats _stats = new();

    public string Name => "DirectFileBaseline";
    public StrategyCapabilities Capabilities => StrategyCapabilities.FullPayload | StrategyCapabilities.FieldProjection | StrategyCapabilities.Persistence;

    public async ValueTask InitializeAsync(string dataDirectory, RecordManifest manifest, StrategyOptions options, CancellationToken ct = default)
    {
        _manifest = manifest;
        _store = new ExtentStore(dataDirectory, manifest.RecordsPerSegment);
        await _store.InitializeAsync(openForReading: true, enableMMap: false, ct).ConfigureAwait(false);
    }

    public async ValueTask<StrategyResult> GetAsync(ulong key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);
        if (_store == null || !_store.TryLookupIndex(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return StrategyResult.Miss;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(entry.Length);
        try
        {
            int read = await _store.ReadRandomAsync(entry.SegmentIndex, entry.Offset, buffer.AsMemory(0, entry.Length), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalReads);
            Interlocked.Add(ref _stats.PhysicalBytesRead, read);

            if (read < entry.Length)
            {
                Interlocked.Increment(ref _stats.Misses);
                return StrategyResult.Miss;
            }

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(buffer.AsSpan(0, RecordHeader.HeaderSize));
            if (header.Magic != RecordHeader.MagicValue || header.Key != key)
            {
                Interlocked.Increment(ref _stats.CorrectnessViolations);
                return StrategyResult.Miss;
            }

            byte[] payload = buffer.AsSpan(RecordHeader.HeaderSize, (int)header.PayloadLength).ToArray();
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.ExtentReads);
            return StrategyResult.FromExtent(payload, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask<StrategyResult> GetProjectionAsync(ulong key, int fieldIndex, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);
        if (_store == null || !_store.TryLookupIndex(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return StrategyResult.Miss;
        }

        // Direct baseline reads the full record header from storage
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(RecordHeader.HeaderSize);
        try
        {
            int read = await _store.ReadRandomAsync(entry.SegmentIndex, entry.Offset, headerBuf.AsMemory(0, RecordHeader.HeaderSize), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalReads);
            Interlocked.Add(ref _stats.PhysicalBytesRead, read);

            if (read < RecordHeader.HeaderSize)
            {
                Interlocked.Increment(ref _stats.Misses);
                return StrategyResult.Miss;
            }

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(headerBuf.AsSpan(0, RecordHeader.HeaderSize));
            if (header.Magic != RecordHeader.MagicValue || header.Key != key)
            {
                Interlocked.Increment(ref _stats.CorrectnessViolations);
                return StrategyResult.Miss;
            }

            ulong val = fieldIndex == 0 ? header.Field1 : header.Field2;
            Interlocked.Increment(ref _stats.Hits);
            return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }
    }

    public async ValueTask<StrategyResult> ComputeDerivedAsync(ulong key, ulong param, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);
        var res = await GetProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;

        // Perform computation after disk read
        ulong computed = (res.ScalarValue * 31UL + param) ^ 0x5555555555555555UL;
        return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, res.BytesRead);
    }

    public async ValueTask<StrategyResult> EvaluatePredicateAsync(ulong key, ulong threshold, CancellationToken ct = default)
    {
        var res = await GetProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        bool matched = res.ScalarValue > threshold;
        return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, res.BytesRead);
    }

    public async ValueTask<bool> UpsertAsync(DataRecord record, CancellationToken ct = default)
    {
        if (_store == null) return false;
        await _store.AppendRecordAsync(record, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _stats.PhysicalWrites);
        Interlocked.Add(ref _stats.PhysicalBytesWritten, record.TotalSerializedSize);
        return true;
    }

    public ValueTask InvalidateAsync(ulong key, CancellationToken ct = default)
    {
        // Direct baseline does not maintain cache
        Interlocked.Increment(ref _stats.Invalidations);
        return ValueTask.CompletedTask;
    }

    public StrategyStats GetStats() => _stats;
    public void ResetStats() => _stats.Hits = _stats.Misses = _stats.TotalRequests = _stats.PhysicalReads = _stats.PhysicalBytesRead = 0;

    public async ValueTask DisposeAsync()
    {
        if (_store != null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }
}
