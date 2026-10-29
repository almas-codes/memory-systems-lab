using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

/// <summary>
/// MEM-09: Dense Internal IDs with Indirection
/// Translates sparse, non-contiguous external IDs into dense contiguous internal IDs (0..M-1) for flat array efficiency.
/// </summary>
public sealed class MEM09_DenseInternalIdsIndirection : IExperimentCandidate, IExactRecordLookup<long, byte>, IMemoryAccountable
{
    public string ExperimentId => "MEM-INDIR-009";
    public string CandidateName => "Dense Internal IDs with Indirection (MEM-09)";
    public string Family => "MemoryAndLocality";
    public string Description => "Maps sparse external IDs to contiguous internal indices, preserving dense flat-array storage.";

    private readonly ConcurrentDictionary<long, int> _externalToInternal = new();
    private readonly byte[] _denseStorage;
    private int _internalIdCounter;
    private readonly int _maxCapacity;

    public int RegisteredCount => _externalToInternal.Count;
    public long TotalAllocatedBytes => _denseStorage.LongLength + (_externalToInternal.Count * 32L);
    public long PayloadBytes => _denseStorage.LongLength;
    public long MetadataBytes => _externalToInternal.Count * 32L;

    public MEM09_DenseInternalIdsIndirection(int maxCapacity = 100_000)
    {
        _maxCapacity = maxCapacity;
        _denseStorage = new byte[maxCapacity];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOrAssignInternalId(long externalId)
    {
        return _externalToInternal.GetOrAdd(externalId, id =>
        {
            int assigned = Interlocked.Increment(ref _internalIdCounter) - 1;
            if (assigned >= _maxCapacity) throw new InvalidOperationException("Dense internal capacity exceeded.");
            return assigned;
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long externalId)
    {
        if (_externalToInternal.TryGetValue(externalId, out int internalId))
        {
            return _denseStorage[internalId];
        }
        return 0; // Not registered
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAnswer(long externalId, byte pattern)
    {
        int internalId = GetOrAssignInternalId(externalId);
        _denseStorage[internalId] = pattern;
    }
}
