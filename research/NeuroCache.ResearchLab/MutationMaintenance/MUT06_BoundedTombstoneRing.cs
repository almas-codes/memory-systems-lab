using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MutationMaintenance;

public readonly record struct TombstoneItem(long Id, bool Answer, long SequenceId);

/// <summary>
/// MUT-06: Bounded Tombstone Ring (BTR)
/// Fixed-capacity ring buffer with hard invariant enforcement: never drops or overwrites unmerged mutations.
/// </summary>
public sealed class MUT06_BoundedTombstoneRing : IExperimentCandidate, IExactRecordLookup<long, bool>, ISourceMutationHandler, ICompactionSupport, IMemoryAccountable
{
    public string ExperimentId => "MUT-BTR-006";
    public string CandidateName => "Bounded Tombstone Ring (BTR)";
    public string Family => "MutationMaintenance";
    public string Description => "Fixed-capacity circular buffer with enforced backpressure and compulsory compaction before overflow.";

    private readonly EapBooleanBitmap _baseBitmap;
    private readonly TombstoneItem[] _ring;
    private readonly int _capacity;
    private int _head;
    private int _tail;
    private int _count;
    private readonly object _ringLock = new();
    private readonly long _maxId;

    public long MaxId => _maxId;
    public int Count { get { lock (_ringLock) return _count; } }
    public int Capacity => _capacity;
    public bool NeedsCompaction { get { lock (_ringLock) return _count >= _capacity * 0.8; } }

    public long TotalAllocatedBytes => _baseBitmap.RawMemoryBytes + (_capacity * 24L);
    public long PayloadBytes => _baseBitmap.RawMemoryBytes;
    public long MetadataBytes => _capacity * 24L;

    public MUT06_BoundedTombstoneRing(long maxId, int capacity = 10_000)
    {
        _maxId = maxId;
        _baseBitmap = new EapBooleanBitmap(maxId);
        _capacity = capacity;
        _ring = new TombstoneItem[capacity];
    }

    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        bool ans = active && balance >= 100;
        lock (_ringLock)
        {
            if (_count >= _capacity)
            {
                // Enforced compaction before accepting write - strictly never overwrites uncompacted data
                CompactUnderLock();
            }

            _ring[_tail] = new TombstoneItem(id, ans, sequenceId);
            _tail = (_tail + 1) % _capacity;
            _count++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        lock (_ringLock)
        {
            // Search ring backwards
            for (int i = 0; i < _count; i++)
            {
                int idx = (_tail - 1 - i + _capacity) % _capacity;
                if (_ring[idx].Id == id) return _ring[idx].Answer;
            }
        }
        return _baseBitmap.IsEligible(id);
    }

    public void Compact()
    {
        lock (_ringLock)
        {
            CompactUnderLock();
        }
    }

    private void CompactUnderLock()
    {
        while (_count > 0)
        {
            var item = _ring[_head];
            _baseBitmap.SetEligible(item.Id, item.Answer);
            _head = (_head + 1) % _capacity;
            _count--;
        }
    }
}
