using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;
using NeuroCache.ResearchLab.Recovery;

namespace NeuroCache.ResearchLab.HybridCompositions;

/// <summary>
/// COMBO-03: Bounded Overlay with Copy-on-Write Generations (COMBO-OVERLAY-COW-003)
/// Composition of:
/// - Low-latency concurrent mutation overlay absorbing write bursts in L1/L2 cache.
/// - Copy-on-Write page directory providing stable lockless snapshots for cold backing store.
/// </summary>
public sealed class COMBO03_BoundedOverlayWithCowGenerations : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "COMBO-OVERLAY-COW-003";
    public string CandidateName => "Bounded Overlay + CoW Page Generations";
    public string Family => "HybridCompositions";
    public string Description => "Absorbs updates in a bounded concurrent overlay before draining to immutable 4KB CoW pages.";

    private readonly long _maxId;
    private readonly REC01_CopyOnWriteAnswerPages _cowStore;
    private readonly ConcurrentDictionary<long, byte> _overlay;
    private readonly int _overlayThreshold;
    private int _isDraining;
    private long _mutationCount;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _cowStore.TotalAllocatedBytes + (long)_overlay.Count * 32;
    public long PayloadBytes => _cowStore.PayloadBytes;
    public long MetadataBytes => _cowStore.MetadataBytes + (long)_overlay.Count * 32;
    public int OverlayCount => _overlay.Count;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public COMBO03_BoundedOverlayWithCowGenerations(long maxId, int overlayThreshold = 10000)
    {
        _maxId = maxId;
        _cowStore = new REC01_CopyOnWriteAnswerPages(maxId);
        _overlay = new ConcurrentDictionary<long, byte>();
        _overlayThreshold = overlayThreshold;
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
        if (_overlay.TryGetValue(id, out byte overlayVal))
        {
            return overlayVal;
        }

        return _cowStore.QueryAll(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        if (id < 0 || id > _maxId) return;
        var r = new AccountRecord(id, balance, active, sequenceId);
        byte pattern = r.ComputeAnswerPattern();

        _overlay[id] = pattern;
        long mut = Interlocked.Increment(ref _mutationCount);

        if ((mut & 4095) == 0 && _overlay.Count >= _overlayThreshold)
        {
            DrainOverlay();
        }
    }

    public void DrainOverlay()
    {
        if (Interlocked.CompareExchange(ref _isDraining, 1, 0) != 0) return;
        try
        {
            int drained = 0;
            foreach (var kvp in _overlay)
            {
                if (_overlay.TryRemove(kvp.Key, out byte pattern))
                {
                    bool active = (pattern & (1 << 7)) != 0;
                    int balance = (pattern & (1 << 2)) != 0 ? 1000 : 0;
                    _cowStore.HandleMutation(kvp.Key, balance, active, 0);
                    if (++drained >= 500) break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _isDraining, 0);
        }
    }
}
