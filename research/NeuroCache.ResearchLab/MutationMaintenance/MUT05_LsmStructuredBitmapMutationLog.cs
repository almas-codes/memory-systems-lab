using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MutationMaintenance;

public readonly record struct LsmMutationEntry(long Id, bool Answer, long SequenceId);

/// <summary>
/// MUT-05: LSM-Structured Bitmap Mutation Log
/// Records mutations in append-only segments, compacting periodically into a base bitmap with checkpointing.
/// </summary>
public sealed class MUT05_LsmStructuredBitmapMutationLog : IExperimentCandidate, IExactRecordLookup<long, bool>, ISourceMutationHandler, ICompactionSupport, IMemoryAccountable
{
    public string ExperimentId => "MUT-LSM-005";
    public string CandidateName => "LSM-Structured Bitmap Mutation Log (MUT-05)";
    public string Family => "MutationMaintenance";
    public string Description => "Append-only memory log segments compacted in batches into an immutable base bitmap.";

    private readonly EapBooleanBitmap _baseBitmap;
    private readonly ConcurrentQueue<LsmMutationEntry> _memTable = new();
    private readonly int _flushThreshold;
    private long _checkpointSequence;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long CheckpointSequence => Volatile.Read(ref _checkpointSequence);
    public int MemTableCount => _memTable.Count;
    public bool NeedsCompaction => _memTable.Count >= _flushThreshold;

    public long TotalAllocatedBytes => _baseBitmap.RawMemoryBytes + (_memTable.Count * 24L);
    public long PayloadBytes => _baseBitmap.RawMemoryBytes;
    public long MetadataBytes => _memTable.Count * 24L;

    public MUT05_LsmStructuredBitmapMutationLog(long maxId, int flushThreshold = 5_000)
    {
        _maxId = maxId;
        _baseBitmap = new EapBooleanBitmap(maxId);
        _flushThreshold = flushThreshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        bool ans = active && balance >= 100;
        _memTable.Enqueue(new LsmMutationEntry(id, ans, sequenceId));

        if (NeedsCompaction)
        {
            Compact();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        // Scan memtable from newest to oldest for single point query (in production, overlay index is used)
        // For simplicity and correctness in test harness:
        var entries = _memTable.ToArray();
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            if (entries[i].Id == id) return entries[i].Answer;
        }
        return _baseBitmap.IsEligible(id);
    }

    public void Compact()
    {
        long highestSeq = _checkpointSequence;
        while (_memTable.TryDequeue(out var entry))
        {
            _baseBitmap.SetEligible(entry.Id, entry.Answer);
            if (entry.SequenceId > highestSeq) highestSeq = entry.SequenceId;
        }
        Volatile.Write(ref _checkpointSequence, highestSeq);
    }
}
