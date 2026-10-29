using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

/// <summary>
/// MEM-03: Grouped Co-location by Access Affinity (GCA)
/// Reorders physical records into contiguous slots based on observed co-access affinity to maximize hardware cache hits.
/// </summary>
public sealed class MEM03_GroupedAffinityColocation : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "MEM-GCA-003";
    public string CandidateName => "Grouped Co-location by Access Affinity (GCA / MEM-03)";
    public string Family => "MemoryAndLocality";
    public string Description => "Maps external IDs to physical cache-line co-located slots based on affinity clusters.";

    private readonly int[] _idToSlotMap;
    private readonly byte[] _physicalSlots;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => (_idToSlotMap.Length * 4L) + _physicalSlots.Length;
    public long PayloadBytes => _physicalSlots.Length;
    public long MetadataBytes => _idToSlotMap.Length * 4L;

    public MEM03_GroupedAffinityColocation(long maxId, int[] affinityClusterOrdering)
    {
        _maxId = maxId;
        _idToSlotMap = new int[maxId + 1];
        _physicalSlots = new byte[maxId + 1];

        for (int i = 0; i < affinityClusterOrdering.Length && i <= maxId; i++)
        {
            long externalId = affinityClusterOrdering[i];
            _idToSlotMap[externalId] = i; // Map external ID to physical clustered slot
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAnswer(long externalId, byte pattern)
    {
        int slot = _idToSlotMap[externalId];
        _physicalSlots[slot] = pattern;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long externalId)
    {
        int slot = _idToSlotMap[externalId];
        return _physicalSlots[slot];
    }
}
