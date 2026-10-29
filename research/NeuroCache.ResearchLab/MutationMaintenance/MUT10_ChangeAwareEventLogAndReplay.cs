using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MutationMaintenance;

public readonly record struct ChangeAwareLogEntry(
    long SequenceId,
    long AccountId,
    bool OldAnswer,
    bool NewAnswer,
    long SourceVersion,
    DateTime TimestampUtc);

/// <summary>
/// MUT-10: Change-Aware Event Log and Replay
/// Combines answer-transition detection with an ordered, replayable event log for recovery and auditing.
/// </summary>
public sealed class MUT10_ChangeAwareEventLogAndReplay : IExperimentCandidate, IRecoveryReplay, IMemoryAccountable
{
    public string ExperimentId => "MUT-REPLAYLOG-010";
    public string CandidateName => "Change-Aware Event Log and Replay (MUT-10)";
    public string Family => "MutationMaintenance";
    public string Description => "Ordered replayable transition log integrating sequence IDs, source versions, and answer transitions.";

    private readonly ConcurrentBag<ChangeAwareLogEntry> _log = new();
    private readonly bool[] _currentAnswers;
    private long _maxSequenceId;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long HighestSequenceId => Volatile.Read(ref _maxSequenceId);
    public int LogCount => _log.Count;

    public long TotalAllocatedBytes => _currentAnswers.LongLength + (_log.Count * 48L);
    public long PayloadBytes => _currentAnswers.LongLength;
    public long MetadataBytes => _log.Count * 48L;

    public MUT10_ChangeAwareEventLogAndReplay(long maxId)
    {
        _maxId = maxId;
        _currentAnswers = new bool[maxId + 1];
    }

    public void RecordEvent(long sequenceId, long accountId, int balance, bool active, long sourceVersion)
    {
        bool newAns = active && balance >= 100;
        bool oldAns = _currentAnswers[accountId];

        if (newAns != oldAns)
        {
            _currentAnswers[accountId] = newAns;
            var entry = new ChangeAwareLogEntry(sequenceId, accountId, oldAns, newAns, sourceVersion, DateTime.UtcNow);
            _log.Add(entry);

            long curMax;
            do
            {
                curMax = Volatile.Read(ref _maxSequenceId);
                if (sequenceId <= curMax) break;
            } while (Interlocked.CompareExchange(ref _maxSequenceId, sequenceId, curMax) != curMax);
        }
    }

    public Task ReplayFromSequenceAsync(long fromSeq, CancellationToken ct = default)
    {
        var ordered = _log.Where(e => e.SequenceId >= fromSeq)
                          .OrderBy(e => e.SequenceId)
                          .ToList();

        foreach (var entry in ordered)
        {
            _currentAnswers[entry.AccountId] = entry.NewAnswer;
        }

        return Task.CompletedTask;
    }

    public bool Query(long id) => _currentAnswers[id];
}
