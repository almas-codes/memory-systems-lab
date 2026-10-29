using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MutationMaintenance;

public readonly record struct AnswerTransitionEvent(long Id, bool OldAnswer, bool NewAnswer, long SequenceId);

/// <summary>
/// MUT-04: Change-Aware Transition Compression (CATC)
/// Filters mutations at the semantic boundary: emits transition events only when computed query answers actually change.
/// </summary>
public sealed class MUT04_ChangeAwareTransitionCompression : IExperimentCandidate, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "MUT-CATC-004";
    public string CandidateName => "Change-Aware Transition Compression (CATC)";
    public string Family => "MutationMaintenance";
    public string Description => "Suppresses redundant source mutations; emits transition events only on answer-state changes.";

    private readonly bool[] _currentAnswers;
    private readonly List<AnswerTransitionEvent> _transitionLog = new();
    private readonly object _logLock = new();
    private long _totalSourceMutations;
    private long _emittedTransitions;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalSourceMutations => Volatile.Read(ref _totalSourceMutations);
    public long EmittedTransitions => Volatile.Read(ref _emittedTransitions);
    public double CompressionRatio => TotalSourceMutations == 0 ? 0 : 1.0 - ((double)EmittedTransitions / TotalSourceMutations);

    public long TotalAllocatedBytes => _currentAnswers.LongLength + (_transitionLog.Count * 24L);
    public long PayloadBytes => _currentAnswers.LongLength;
    public long MetadataBytes => _transitionLog.Count * 24L;

    public MUT04_ChangeAwareTransitionCompression(long maxId)
    {
        _maxId = maxId;
        _currentAnswers = new bool[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        Interlocked.Increment(ref _totalSourceMutations);
        bool newAnswer = active && balance >= 100;
        bool oldAnswer = _currentAnswers[id];

        if (newAnswer != oldAnswer)
        {
            _currentAnswers[id] = newAnswer;
            Interlocked.Increment(ref _emittedTransitions);
            lock (_logLock)
            {
                _transitionLog.Add(new AnswerTransitionEvent(id, oldAnswer, newAnswer, sequenceId));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id) => _currentAnswers[id];

    public IReadOnlyList<AnswerTransitionEvent> GetTransitions()
    {
        lock (_logLock) return _transitionLog.ToArray();
    }
}
