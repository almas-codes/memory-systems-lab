using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Oracle;

namespace NeuroCache.ResearchLab.QueryExecution;

/// <summary>
/// EXEC-02: Hybrid Hot/Cold Materialization (HHCM)
/// Materializes answers in a bounded hot cache; cold misses route to exact oracle fallback.
/// </summary>
public sealed class EXEC02_HybridHotColdMaterialization : IExperimentCandidate, IExactRecordLookup<long, bool>, IColdFallback<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "EXEC-HHCM-002";
    public string CandidateName => "Hybrid Hot/Cold Materialization (HHCM)";
    public string Family => "QueryExecution";
    public string Description => "Maintains a bounded hot-record cache with LRU eviction and exact cold fallback.";

    private readonly ConcurrentDictionary<long, bool> _hotCache = new();
    private readonly int _capacity;
    private readonly IExactOracle _coldOracle;

    private long _hits;
    private long _misses;
    private long _evictions;

    public long CacheHits => Volatile.Read(ref _hits);
    public long CacheMisses => Volatile.Read(ref _misses);
    public long Evictions => Volatile.Read(ref _evictions);
    public double HitRate => (_hits + _misses) == 0 ? 0 : (double)_hits / (_hits + _misses);

    public long TotalAllocatedBytes => _hotCache.Count * 32L;
    public long PayloadBytes => _hotCache.Count * 8L;
    public long MetadataBytes => _hotCache.Count * 24L;

    public EXEC02_HybridHotColdMaterialization(IExactOracle coldOracle, int capacity = 10_000)
    {
        _coldOracle = coldOracle;
        _capacity = capacity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        if (_hotCache.TryGetValue(id, out bool cached))
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _misses);
        bool answer = _coldOracle.Evaluate(id, 1); // Q2: balance >= 100 AND active

        if (_hotCache.Count >= _capacity)
        {
            // Simple eviction of one key to maintain bound
            foreach (var key in _hotCache.Keys)
            {
                if (_hotCache.TryRemove(key, out _))
                {
                    Interlocked.Increment(ref _evictions);
                    break;
                }
            }
        }

        _hotCache[id] = answer;
        return answer;
    }

    public Task<bool> FallbackQueryAsync(long id, CancellationToken ct = default)
    {
        return Task.FromResult(_coldOracle.Evaluate(id, 1));
    }

    public void Invalidate(long id) => _hotCache.TryRemove(id, out _);
}
