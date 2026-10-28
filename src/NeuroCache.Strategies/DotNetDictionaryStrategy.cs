using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Strategies;

public sealed class DotNetDictionaryStrategy : IStorageStrategy
{
    private sealed class CachedEntry
    {
        public ulong Key;
        public uint Version;
        public ulong Field1;
        public ulong Field2;
        public byte[] Payload = Array.Empty<byte>();

        public int EstimatedBytes => 64 + Payload.Length;
    }

    private readonly ConcurrentDictionary<ulong, CachedEntry> _map = new();
    private ExtentStore? _store;
    private long _residentBytes;
    private long _budgetLimit;
    private readonly StrategyStats _stats = new();

    public string Name => "DotNetDictionary";
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

        if (_map.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            return StrategyResult.FromInline(entry.Payload);
        }

        // Cache miss: load from extent store and attempt to cache if under budget
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

        // Check if adding this entry would exceed RAM budget
        int estSize = 64 + record.Payload.Length;
        if (Interlocked.Read(ref _residentBytes) + estSize <= _budgetLimit)
        {
            var cached = new CachedEntry
            {
                Key = record.Key,
                Version = record.Version,
                Field1 = record.Field1,
                Field2 = record.Field2,
                Payload = record.Payload
            };

            if (_map.TryAdd(key, cached))
            {
                Interlocked.Add(ref _residentBytes, estSize);
                _stats.ResidentBytes = _residentBytes;
            }
        }
        else
        {
            Interlocked.Increment(ref _stats.Fallbacks);
        }

        Interlocked.Increment(ref _stats.Hits);
        return StrategyResult.FromExtent(record.Payload, read);
    }

    public async ValueTask<StrategyResult> GetProjectionAsync(ulong key, int fieldIndex, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_map.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            ulong val = fieldIndex == 0 ? entry.Field1 : entry.Field2;
            return StrategyResult.FromInline(val);
        }

        // Fallback to GetAsync to load and extract
        var res = await GetAsync(key, ct).ConfigureAwait(false);
        return res;
    }

    public async ValueTask<StrategyResult> ComputeDerivedAsync(ulong key, ulong param, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stats.TotalRequests);

        if (_map.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            ulong derived = (entry.Field1 * 31UL + param) ^ 0x5555555555555555UL;
            return StrategyResult.FromInline(derived);
        }

        var res = await GetAsync(key, ct).ConfigureAwait(false);
        return res;
    }

    public async ValueTask<StrategyResult> EvaluatePredicateAsync(ulong key, ulong threshold, CancellationToken ct = default)
    {
        if (_map.TryGetValue(key, out var entry))
        {
            Interlocked.Increment(ref _stats.Hits);
            Interlocked.Increment(ref _stats.InlineReuses);
            return StrategyResult.FromInline(entry.Field1 > threshold ? 1UL : 0UL);
        }

        var res = await GetAsync(key, ct).ConfigureAwait(false);
        return res;
    }

    public async ValueTask<bool> UpsertAsync(DataRecord record, CancellationToken ct = default)
    {
        if (_store != null)
        {
            await _store.AppendRecordAsync(record, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _stats.PhysicalWrites);
            Interlocked.Add(ref _stats.PhysicalBytesWritten, record.TotalSerializedSize);
        }

        int estSize = 64 + record.Payload.Length;
        var cached = new CachedEntry
        {
            Key = record.Key,
            Version = record.Version,
            Field1 = record.Field1,
            Field2 = record.Field2,
            Payload = record.Payload
        };

        if (_map.TryGetValue(record.Key, out var old))
        {
            _map[record.Key] = cached;
            Interlocked.Add(ref _residentBytes, cached.EstimatedBytes - old.EstimatedBytes);
        }
        else if (Interlocked.Read(ref _residentBytes) + estSize <= _budgetLimit)
        {
            if (_map.TryAdd(record.Key, cached))
            {
                Interlocked.Add(ref _residentBytes, estSize);
            }
        }

        _stats.ResidentBytes = _residentBytes;
        return true;
    }

    public ValueTask InvalidateAsync(ulong key, CancellationToken ct = default)
    {
        if (_map.TryRemove(key, out var old))
        {
            Interlocked.Add(ref _residentBytes, -old.EstimatedBytes);
            Interlocked.Increment(ref _stats.Invalidations);
            _stats.ResidentBytes = _residentBytes;
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
        _map.Clear();
        if (_store != null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }
}
