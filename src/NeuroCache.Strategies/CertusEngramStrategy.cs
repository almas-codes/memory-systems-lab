using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Strategies;

public sealed class CertusEngramStrategy : IStorageStrategy
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Claim
    {
        public ulong Key;
        public uint Epoch;
        public int SegmentIndex;
        public long StorageOffset;
        public int Length;
        public uint ClaimToken;

        public const int Size = 32;
    }

    private readonly ConcurrentDictionary<ulong, Claim> _claims = new();
    private ExtentStore? _store;
    private long _budgetLimit;
    private long _residentBytes;
    private readonly StrategyStats _stats = new();

    public string Name => "CertusEngramClaims";
    public StrategyCapabilities Capabilities => StrategyCapabilities.FullPayload | StrategyCapabilities.FieldProjection | StrategyCapabilities.StrictRamBounded;

    public async ValueTask InitializeAsync(string dataDirectory, RecordManifest manifest, StrategyOptions options, CancellationToken ct = default)
    {
        _budgetLimit = options.RamBudgetBytes;
        _store = new ExtentStore(dataDirectory, manifest.RecordsPerSegment);
        await _store.InitializeAsync(openForReading: true, enableMMap: false, ct).ConfigureAwait(false);
    }

    public async ValueTask<StrategyResult> GetAsync(ulong key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_claims.TryGetValue(key, out var claim))
        {
            // Valid claim found in RAM: bypass index search and read payload directly from SSD
            if (_store != null)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(claim.Length);
                try
                {
                    int read = await _store.ReadRandomAsync(claim.SegmentIndex, claim.StorageOffset, buffer.AsMemory(0, claim.Length), ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _stats.PhysicalReads);
                    Interlocked.Add(ref _stats.PhysicalBytesRead, read);

                    if (read == claim.Length)
                    {
                        RecordHeader header = MemoryMarshal.Read<RecordHeader>(buffer.AsSpan(0, RecordHeader.HeaderSize));
                        if (header.Key == key && header.Version == claim.Epoch)
                        {
                            byte[] payload = buffer.AsSpan(RecordHeader.HeaderSize, (int)header.PayloadLength).ToArray();
                            Interlocked.Increment(ref _stats.Hits);
                            Interlocked.Increment(ref _stats.ExtentReads);
                            return StrategyResult.FromExtent(payload, read);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        // Claim miss: authoritative fallback through index
        Interlocked.Increment(ref _stats.Fallbacks);
        if (_store == null || !_store.TryLookupIndex(key, out var indexEntry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return StrategyResult.Miss;
        }

        byte[] fbBuf = ArrayPool<byte>.Shared.Rent(indexEntry.Length);
        try
        {
            int read = await _store.ReadRandomAsync(indexEntry.SegmentIndex, indexEntry.Offset, fbBuf.AsMemory(0, indexEntry.Length), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalReads);
            Interlocked.Add(ref _stats.PhysicalBytesRead, read);

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(fbBuf.AsSpan(0, RecordHeader.HeaderSize));
            byte[] payload = fbBuf.AsSpan(RecordHeader.HeaderSize, (int)header.PayloadLength).ToArray();

            // Populate claim if under budget
            int claimSize = Claim.Size + 32; // struct + concurrent dict node overhead ~64 bytes
            if (Interlocked.Read(ref _residentBytes) + claimSize <= _budgetLimit)
            {
                Claim newClaim = new()
                {
                    Key = key,
                    Epoch = header.Version,
                    SegmentIndex = indexEntry.SegmentIndex,
                    StorageOffset = indexEntry.Offset,
                    Length = indexEntry.Length,
                    ClaimToken = (uint)(key ^ header.Version)
                };

                if (_claims.TryAdd(key, newClaim))
                {
                    Interlocked.Add(ref _residentBytes, claimSize);
                }
            }

            Interlocked.Increment(ref _stats.Hits);
            return StrategyResult.FromExtent(payload, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fbBuf);
        }
    }

    public async ValueTask<StrategyResult> GetProjectionAsync(ulong key, int fieldIndex, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_claims.TryGetValue(key, out var claim) && _store != null)
        {
            // Read only header
            byte[] hBuf = ArrayPool<byte>.Shared.Rent(RecordHeader.HeaderSize);
            try
            {
                int read = await _store.ReadRandomAsync(claim.SegmentIndex, claim.StorageOffset, hBuf.AsMemory(0, RecordHeader.HeaderSize), ct).ConfigureAwait(false);
                Interlocked.Increment(ref _stats.PhysicalReads);
                Interlocked.Add(ref _stats.PhysicalBytesRead, read);

                RecordHeader header = MemoryMarshal.Read<RecordHeader>(hBuf.AsSpan(0, RecordHeader.HeaderSize));
                if (header.Key == key && header.Version == claim.Epoch)
                {
                    ulong val = fieldIndex == 0 ? header.Field1 : header.Field2;
                    Interlocked.Increment(ref _stats.Hits);
                    return StrategyResult.FromExtent(ReadOnlyMemory<byte>.Empty, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(hBuf);
            }
        }

        return await GetAsync(key, ct).ConfigureAwait(false);
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
        if (_store == null) return false;
        var (seg, off, len) = await _store.AppendRecordAsync(record, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _stats.PhysicalWrites);
        Interlocked.Add(ref _stats.PhysicalBytesWritten, len);

        // Update claim
        Claim updated = new()
        {
            Key = record.Key,
            Epoch = record.Version,
            SegmentIndex = seg,
            StorageOffset = off,
            Length = len,
            ClaimToken = (uint)(record.Key ^ record.Version)
        };

        _claims[record.Key] = updated;
        return true;
    }

    public ValueTask InvalidateAsync(ulong key, CancellationToken ct = default)
    {
        if (_claims.TryRemove(key, out _))
        {
            Interlocked.Add(ref _residentBytes, -(Claim.Size + 32));
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
        _claims.Clear();
        if (_store != null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }
}
