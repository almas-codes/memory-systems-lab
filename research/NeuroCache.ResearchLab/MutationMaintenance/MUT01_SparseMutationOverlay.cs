using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MutationMaintenance;

/// <summary>
/// MUT-01: Sparse Mutation Overlay (SMO)
/// Reads check the concurrency-safe overlay first; if absent, falls back to the immutable base bitmap.
/// Compacts overlay into base when overlay count exceeds threshold.
/// </summary>
public sealed class MUT01_SparseMutationOverlay : IExperimentCandidate, IExactRecordLookup<long, bool>, ISourceMutationHandler, ICompactionSupport, IMemoryAccountable
{
    public string ExperimentId => "MUT-SMO-001";
    public string CandidateName => "Sparse Mutation Overlay (SMO)";
    public string Family => "MutationMaintenance";
    public string Description => "Maintains an immutable base bitmap plus a concurrent mutation overlay with bounded compaction.";

    private readonly EapBooleanBitmap _baseBitmap;
    private readonly ConcurrentDictionary<long, bool> _overlay = new();
    private readonly int _maxOverlayCapacity;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public int OverlayCount => _overlay.Count;
    public bool NeedsCompaction => _overlay.Count >= _maxOverlayCapacity;

    public long TotalAllocatedBytes => _baseBitmap.RawMemoryBytes + (_overlay.Count * 32L);
    public long PayloadBytes => _baseBitmap.RawMemoryBytes;
    public long MetadataBytes => _overlay.Count * 32L;

    public MUT01_SparseMutationOverlay(long maxId, int maxOverlayCapacity = 10_000)
    {
        _maxId = maxId;
        _baseBitmap = new EapBooleanBitmap(maxId);
        _maxOverlayCapacity = maxOverlayCapacity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        if (_overlay.TryGetValue(id, out bool overlayVal))
        {
            return overlayVal;
        }
        return _baseBitmap.IsEligible(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        bool ans = active && balance >= 100;
        _overlay[id] = ans;

        if (NeedsCompaction)
        {
            Compact();
        }
    }

    public void Compact()
    {
        foreach (var kvp in _overlay)
        {
            _baseBitmap.SetEligible(kvp.Key, kvp.Value);
            _overlay.TryRemove(kvp.Key, out _);
        }
    }
}
