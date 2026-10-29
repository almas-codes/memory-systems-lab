using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.HybridCompositions;

/// <summary>
/// COMBO-05: Hot Materialization with Exact Cold Fallback (COMBO-HOT-COLD-005)
/// Composition of:
/// - Precomputed hot answer patterns for frequently queried accounts.
/// - Dynamic frequency tracking for admission control.
/// - Cold fallback calculating answers on the fly from backing records.
/// </summary>
public sealed class COMBO05_HotMaterializationWithExactColdFallback : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "COMBO-HOT-COLD-005";
    public string CandidateName => "Hot Materialization + Exact Cold Fallback";
    public string Family => "HybridCompositions";
    public string Description => "Maintains hot precomputed answers in L1/L2 cache; cold accounts evaluate on demand.";

    private readonly long _maxId;
    private readonly int _hotLimit;
    private readonly int _admissionThreshold;
    private readonly ConcurrentDictionary<long, byte> _hotStore;
    private readonly ConcurrentDictionary<long, int> _accessCounts;
    private readonly AccountRecord[] _backingStore;
    private long _mutationCount;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => (long)_hotStore.Count * 32 + (long)_accessCounts.Count * 16 + (long)_backingStore.LongLength * 32;
    public long PayloadBytes => (long)_hotStore.Count * 32;
    public long MetadataBytes => (long)_accessCounts.Count * 16;
    public int HotCount => _hotStore.Count;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public COMBO05_HotMaterializationWithExactColdFallback(long maxId, int hotLimit = 100000, int admissionThreshold = 5)
    {
        _maxId = maxId;
        _hotLimit = hotLimit;
        _admissionThreshold = admissionThreshold;
        _hotStore = new ConcurrentDictionary<long, byte>();
        _accessCounts = new ConcurrentDictionary<long, int>();
        _backingStore = new AccountRecord[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        byte pattern = QueryAll(id);
        return (pattern & (1 << (7 - predicateIndex))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        if (id < 0 || id > _maxId) return 0;

        if (_hotStore.TryGetValue(id, out byte answer))
        {
            return answer;
        }

        byte computed = _backingStore[id].ComputeAnswerPattern();

        // Sample access frequency to avoid dictionary contention
        if ((id & 31) == 0)
        {
            int count = _accessCounts.AddOrUpdate(id, 1, (_, c) => c + 1);
            if (count >= _admissionThreshold && _hotStore.Count < _hotLimit)
            {
                _hotStore.TryAdd(id, computed);
            }
        }

        return computed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        if (id < 0 || id > _maxId) return;

        var rec = new AccountRecord(id, balance, active, sequenceId);
        _backingStore[id] = rec;

        if (_hotStore.ContainsKey(id))
        {
            _hotStore[id] = rec.ComputeAnswerPattern();
        }

        Interlocked.Increment(ref _mutationCount);
    }
}
