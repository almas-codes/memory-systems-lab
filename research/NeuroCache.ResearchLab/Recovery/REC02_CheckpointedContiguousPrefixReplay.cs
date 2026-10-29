using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.Recovery;

/// <summary>
/// REC-02: Checkpointed Contiguous Prefix Replay (REC-CCPR-002)
/// Snapshots state periodically along with the contiguous watermark.
/// On recovery, restores from the checkpoint snapshot and replays from sequence W+1.
/// </summary>
public sealed class REC02_CheckpointedContiguousPrefixReplay : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable, IRecoveryReplay
{
    public string ExperimentId => "REC-CCPR-002";
    public string CandidateName => "Checkpointed Contiguous Prefix Replay (REC-02)";
    public string Family => "Recovery";
    public string Description => "State snapshots paired with contiguous outbox log replay from the safe watermark.";

    private readonly long _maxId;
    private readonly byte[] _answers;
    private readonly ContiguousWatermarkTracker _watermarkTracker;
    private long _checkpointWatermark;
    private byte[] _checkpointData;
    private long _mutationCount;

    public long MaxId => _maxId;
    public long CheckpointWatermark => Volatile.Read(ref _checkpointWatermark);
    public long CurrentWatermark => _watermarkTracker.Watermark;
    public long TotalAllocatedBytes => (_maxId + 1) * 2 + 128;
    public long PayloadBytes => _maxId + 1;
    public long MetadataBytes => (_maxId + 1) + 128;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public REC02_CheckpointedContiguousPrefixReplay(long maxId, long initialWatermark = 0)
    {
        _maxId = maxId;
        _answers = new byte[maxId + 1];
        _checkpointData = new byte[maxId + 1];
        _watermarkTracker = new ContiguousWatermarkTracker(initialWatermark);
        _checkpointWatermark = initialWatermark;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        if (id < 0 || id > _maxId) return false;
        byte pattern = Volatile.Read(ref _answers[id]);
        return (pattern & (1 << (7 - predicateIndex))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        if (id < 0 || id > _maxId) return 0;
        return Volatile.Read(ref _answers[id]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        if (id < 0 || id > _maxId) return;
        var r = new AccountRecord(id, balance, active, sequenceId);
        byte pattern = r.ComputeAnswerPattern();

        _watermarkTracker.RegisterInFlight(sequenceId);
        Volatile.Write(ref _answers[id], pattern);
        _watermarkTracker.AcknowledgePublication(sequenceId);

        Interlocked.Increment(ref _mutationCount);
    }

    public void TakeCheckpoint()
    {
        long safeWatermark = _watermarkTracker.Watermark;
        byte[] snapshot = new byte[_maxId + 1];
        Array.Copy(_answers, snapshot, _answers.Length);

        _checkpointData = snapshot;
        Volatile.Write(ref _checkpointWatermark, safeWatermark);
    }

    public long RestoreFromCheckpoint()
    {
        long cpWatermark = Volatile.Read(ref _checkpointWatermark);
        Array.Copy(_checkpointData, _answers, _answers.Length);
        _watermarkTracker.Reset(cpWatermark);
        return cpWatermark;
    }

    public int ReplayOutbox(IEnumerable<(long SeqId, long AccountId, int Balance, bool Active)> outboxEntries)
    {
        int replayedCount = 0;
        long currentWatermark = _watermarkTracker.Watermark;

        foreach (var entry in outboxEntries.OrderBy(e => e.SeqId))
        {
            if (entry.SeqId <= currentWatermark)
            {
                continue;
            }

            HandleMutation(entry.AccountId, entry.Balance, entry.Active, entry.SeqId);
            replayedCount++;
        }

        return replayedCount;
    }

    public Task ReplayFromSequenceAsync(long fromSeq, CancellationToken ct = default)
    {
        RestoreFromCheckpoint();
        return Task.CompletedTask;
    }
}
