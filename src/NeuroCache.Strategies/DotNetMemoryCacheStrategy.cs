using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Strategies;

public sealed class DotNetMemoryCacheStrategy : IStorageStrategy
{
    private sealed class CachedPayload
    {
        public ulong Key;
        public uint Version;
        public ulong Field1;
        public ulong Field2;
        public byte[] Payload = Array.Empty<byte>();
        public int SizeBytes => 80 + Payload.Length;
    }

    private MemoryCache? _cache;
    private ExtentStore? _store;
    private long _budgetBytes;
    private readonly StrategyStats _stats = new();

    public string Name => "DotNetMemoryCache";
    public StrategyCapabilities Capabilities => StrategyCapabilities.FullPayload | StrategyCapabilities.FieldProjection | StrategyCapabilities.StrictRamBounded;

    public async ValueTask InitializeAsync(string dataDirectory, RecordManifest manifest, StrategyOptions options, CancellationToken ct = default)
    {
        _budgetBytes = options.RamBudgetBytes;

        var cacheOptions = new MemoryCacheOptions
        {
            SizeLimit = _budgetBytes,
            CompactionPercentage = 0.1
        };

        _cache = new MemoryCache(cacheOptions);
        _store = new ExtentStore(dataDirectory, manifest.RecordsPerSegment);
        await _store.InitializeAsync(openForReading: true, enableMMap: false, ct).ConfigureAwait(false);
    }

    public async ValueTask<StrategyResult> GetAsync(ulong key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_cache != null && _cache.TryGetValue(key, out CachedPayload? cached) && cached != null)
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            return StrategyResult.FromInline(cached.Payload);
        }

        if (_store == null || !_store.TryLookupIndex(key, out var indexEntry))
        {
            Interlocked.Increment(ref _stats.Misses);
            return StrategyResult.Miss;
        }

        byte[] buf = new byte[indexEntry.Length];
        int read = await _store.ReadRandomAsync(indexEntry.SegmentIndex, indexEntry.Offset, buf, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _stats.PhysicalReads);
        Interlocked.Add(ref _stats.PhysicalBytesRead, read);

        var record = DataRecord.ReadFrom(buf);
        var entry = new CachedPayload
        {
            Key = record.Key,
            Version = record.Version,
            Field1 = record.Field1,
            Field2 = record.Field2,
            Payload = record.Payload
        };

        if (_cache != null)
        {
            var entryOptions = new MemoryCacheEntryOptions()
                .SetSize(entry.SizeBytes)
                .RegisterPostEvictionCallback((k, v, r, s) => Interlocked.Increment(ref _stats.Evictions));

            _cache.Set(key, entry, entryOptions);
        }

        Interlocked.Increment(ref _stats.Hits);
        return StrategyResult.FromExtent(record.Payload, read);
    }

    public async ValueTask<StrategyResult> GetProjectionAsync(ulong key, int fieldIndex, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_cache != null && _cache.TryGetValue(key, out CachedPayload? cached) && cached != null)
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            ulong val = fieldIndex == 0 ? cached.Field1 : cached.Field2;
            return StrategyResult.FromInline(val);
        }

        return await GetAsync(key, ct).ConfigureAwait(false);
    }

    public async ValueTask<StrategyResult> ComputeDerivedAsync(ulong key, ulong param, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_cache != null && _cache.TryGetValue(key, out CachedPayload? cached) && cached != null)
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            ulong derived = (cached.Field1 * 31UL + param) ^ 0x5555555555555555UL;
            return StrategyResult.FromInline(derived);
        }

        return await GetAsync(key, ct).ConfigureAwait(false);
    }

    public async ValueTask<StrategyResult> EvaluatePredicateAsync(ulong key, ulong threshold, CancellationToken ct = default)
    {
        if (_cache != null && _cache.TryGetValue(key, out CachedPayload? cached) && cached != null)
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            return StrategyResult.FromInline(cached.Field1 > threshold ? 1UL : 0UL);
        }

        return await GetAsync(key, ct).ConfigureAwait(false);
    }

    public async ValueTask<bool> UpsertAsync(DataRecord record, CancellationToken ct = default)
    {
        if (_store != null)
        {
            await _store.AppendRecordAsync(record, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalWrites);
            Interlocked.Add(ref _stats.PhysicalBytesWritten, record.TotalSerializedSize);
        }

        if (_cache != null)
        {
            var entry = new CachedPayload
            {
                Key = record.Key,
                Version = record.Version,
                Field1 = record.Field1,
                Field2 = record.Field2,
                Payload = record.Payload
            };

            var entryOptions = new MemoryCacheEntryOptions()
                .SetSize(entry.SizeBytes)
                .RegisterPostEvictionCallback((k, v, r, s) => Interlocked.Increment(ref _stats.Evictions));

            _cache.Set(record.Key, entry, entryOptions);
        }

        return true;
    }

    public ValueTask InvalidateAsync(ulong key, CancellationToken ct = default)
    {
        _cache?.Remove(key);
        Interlocked.Increment(ref _stats.Invalidations);
        return ValueTask.CompletedTask;
    }

    public StrategyStats GetStats()
    {
        if (_cache != null)
        {
            _stats.ResidentBytes = (long)(_cache.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0);
        }
        return _stats;
    }

    public void ResetStats()
    {
        _stats.Hits = _stats.Misses = _stats.TotalRequests = _stats.PhysicalReads = _stats.PhysicalBytesRead = 0;
    }

    public async ValueTask DisposeAsync()
    {
        _cache?.Dispose();
        _cache = null;

        if (_store != null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }
}
