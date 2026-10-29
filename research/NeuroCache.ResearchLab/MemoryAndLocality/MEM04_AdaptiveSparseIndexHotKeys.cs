using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

/// <summary>
/// MEM-04: Adaptive Sparse Index for Hot Keys (ASI)
/// Maintains a small bounded fast-index for highly contended hot keys to absorb write bursts before merging.
/// </summary>
public sealed class MEM04_AdaptiveSparseIndexHotKeys : IExperimentCandidate, IExactRecordLookup<long, byte>, IMemoryAccountable
{
    public string ExperimentId => "MEM-ASI-004";
    public string CandidateName => "Adaptive Sparse Index for Hot Keys (ASI / MEM-04)";
    public string Family => "MemoryAndLocality";
    public string Description => "Maintains a bounded fast hash index for contended keys before background merging into base array.";

    private readonly byte[] _baseStore;
    private readonly ConcurrentDictionary<long, byte> _hotKeyIndex = new();
    private readonly int _maxHotKeys;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public int HotKeyCount => _hotKeyIndex.Count;

    public long TotalAllocatedBytes => _baseStore.LongLength + (_hotKeyIndex.Count * 32L);
    public long PayloadBytes => _baseStore.LongLength;
    public long MetadataBytes => _hotKeyIndex.Count * 32L;

    public MEM04_AdaptiveSparseIndexHotKeys(long maxId, int maxHotKeys = 10_000)
    {
        _maxId = maxId;
        _baseStore = new byte[maxId + 1];
        _maxHotKeys = maxHotKeys;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        if (_hotKeyIndex.TryGetValue(id, out byte val)) return val;
        return _baseStore[id];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHotKey(long id, byte val)
    {
        if (_hotKeyIndex.Count >= _maxHotKeys)
        {
            MergeHotKeysToBase();
        }
        _hotKeyIndex[id] = val;
    }

    public void MergeHotKeysToBase()
    {
        foreach (var kvp in _hotKeyIndex)
        {
            _baseStore[kvp.Key] = kvp.Value;
            _hotKeyIndex.TryRemove(kvp.Key, out _);
        }
    }
}
