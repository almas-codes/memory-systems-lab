using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Strategies;

public sealed class SufficiencyFabricStrategy : IStorageStrategy
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ValidityCertificate
    {
        public ulong Key;
        public uint Version;
        public ulong Seed1;
        public ulong Seed2;
        public ulong PayloadFingerprint;

        public const int Size = 36;
    }

    private readonly ConcurrentDictionary<ulong, ValidityCertificate> _certificates = new();
    private ExtentStore? _store;
    private long _budgetLimit;
    private long _residentBytes;
    private readonly StrategyStats _stats = new();

    public string Name => "SufficiencyFabric";
    public StrategyCapabilities Capabilities => StrategyCapabilities.FieldProjection | StrategyCapabilities.PureRecomputation | StrategyCapabilities.FullPayload | StrategyCapabilities.StrictRamBounded;

    public async ValueTask InitializeAsync(string dataDirectory, RecordManifest manifest, StrategyOptions options, CancellationToken ct = default)
    {
        _budgetLimit = options.RamBudgetBytes;
        _store = new ExtentStore(dataDirectory, manifest.RecordsPerSegment);
        await _store.InitializeAsync(openForReading: true, enableMMap: false, ct).ConfigureAwait(false);
    }

    public async ValueTask<StrategyResult> GetAsync(ulong key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        // Sufficiency fabric does not keep full payloads in RAM; it reads from disk on demand and validates certificate
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

            // Check if certificate exists and matches
            if (_certificates.TryGetValue(key, out var cert))
            {
                if (cert.Version != header.Version || cert.PayloadFingerprint != header.Checksum)
                {
                    Interlocked.Increment(ref _stats.CorrectnessViolations);
                    // invalidate stale certificate
                    _certificates.TryRemove(key, out _);
                }
            }
            else
            {
                // Register certificate if under budget
                int certSize = ValidityCertificate.Size + 32;
                if (Interlocked.Read(ref _residentBytes) + certSize <= _budgetLimit)
                {
                    var newCert = new ValidityCertificate
                    {
                        Key = key,
                        Version = header.Version,
                        Seed1 = header.Field1,
                        Seed2 = header.Field2,
                        PayloadFingerprint = header.Checksum
                    };

                    if (_certificates.TryAdd(key, newCert))
                    {
                        Interlocked.Add(ref _residentBytes, certSize);
                    }
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

    public ValueTask<StrategyResult> GetProjectionAsync(ulong key, int fieldIndex, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_certificates.TryGetValue(key, out var cert))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            ulong val = fieldIndex == 0 ? cert.Seed1 : cert.Seed2;
            return ValueTask.FromResult(StrategyResult.FromInline(val));
        }

        // Cache miss: demand-driven fetch
        return DemandFetchProjectionAsync(key, fieldIndex, ct);
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

            int certSize = ValidityCertificate.Size + 32;
            if (Interlocked.Read(ref _residentBytes) + certSize <= _budgetLimit)
            {
                var newCert = new ValidityCertificate
                {
                    Key = key,
                    Version = header.Version,
                    Seed1 = header.Field1,
                    Seed2 = header.Field2,
                    PayloadFingerprint = header.Checksum
                };

                if (_certificates.TryAdd(key, newCert))
                {
                    Interlocked.Add(ref _residentBytes, certSize);
                }
            }

            Interlocked.Increment(ref _stats.Hits);
            ulong val = fieldIndex == 0 ? header.Field1 : header.Field2;
            return StrategyResult.FromExtent(val, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hBuf);
        }
    }

    public ValueTask<StrategyResult> ComputeDerivedAsync(ulong key, ulong param, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_certificates.TryGetValue(key, out var cert))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.Recomputes);
            ulong derived = (cert.Seed1 * 31UL + param) ^ 0x5555555555555555UL;
            return ValueTask.FromResult(StrategyResult.FromRecompute(derived));
        }

        return DemandFetchComputeAsync(key, param, ct);
    }

    private async ValueTask<StrategyResult> DemandFetchComputeAsync(ulong key, ulong param, CancellationToken ct)
    {
        var res = await DemandFetchProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        ulong derived = (res.ScalarValue * 31UL + param) ^ 0x5555555555555555UL;
        return StrategyResult.FromExtent(derived, res.BytesRead);
    }

    public ValueTask<StrategyResult> EvaluatePredicateAsync(ulong key, ulong threshold, CancellationToken ct = default)
    {
        if (_certificates.TryGetValue(key, out var cert))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.Recomputes);
            return ValueTask.FromResult(StrategyResult.FromRecompute(cert.Seed1 > threshold ? 1UL : 0UL));
        }

        return DemandFetchPredicateAsync(key, threshold, ct);
    }

    private async ValueTask<StrategyResult> DemandFetchPredicateAsync(ulong key, ulong threshold, CancellationToken ct)
    {
        var res = await DemandFetchProjectionAsync(key, 0, ct).ConfigureAwait(false);
        if (!res.Success) return res;
        ulong pred = res.ScalarValue > threshold ? 1UL : 0UL;
        return StrategyResult.FromExtent(pred, res.BytesRead);
    }

    public async ValueTask<bool> UpsertAsync(DataRecord record, CancellationToken ct = default)
    {
        if (_store == null) return false;
        var (seg, off, len) = await _store.AppendRecordAsync(record, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _stats.PhysicalWrites);
        Interlocked.Add(ref _stats.PhysicalBytesWritten, len);

        var cert = new ValidityCertificate
        {
            Key = record.Key,
            Version = record.Version,
            Seed1 = record.Field1,
            Seed2 = record.Field2,
            PayloadFingerprint = record.CalculateChecksum()
        };

        _certificates[record.Key] = cert;
        return true;
    }

    public ValueTask InvalidateAsync(ulong key, CancellationToken ct = default)
    {
        if (_certificates.TryRemove(key, out _))
        {
            Interlocked.Add(ref _residentBytes, -(ValidityCertificate.Size + 32));
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
        _certificates.Clear();
        if (_store != null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }
}
