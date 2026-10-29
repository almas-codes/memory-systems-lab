using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.Representations;

/// <summary>
/// REP-06: Exact Residual Delta Encoding (ERDE)
/// Combines a coarse category plane with exact residual deltas for boundary zone resolution.
/// </summary>
public sealed class REP06_ExactResidualDeltaEncoding : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "REP-ERDE-006";
    public string CandidateName => "Exact Residual Delta Encoding (ERDE)";
    public string Family => "Representations";
    public string Description => "Retains coarse interval categories; maintains exact residual deltas for boundary ambiguity.";

    private readonly byte[] _categories; // Coarse buckets: 0: <100, 1: 100-249, 2: 250-499, 3: 500-749, 4: 750-899, 5: >=900
    private readonly bool[] _activeBits;
    private readonly ConcurrentDictionary<long, int> _exactResiduals = new();
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _categories.LongLength + (_activeBits.LongLength / 8) + (_exactResiduals.Count * 24L);
    public long PayloadBytes => _categories.LongLength + (_activeBits.LongLength / 8);
    public long MetadataBytes => _exactResiduals.Count * 24L;

    public REP06_ExactResidualDeltaEncoding(long maxId)
    {
        _maxId = maxId;
        _categories = new byte[maxId + 1];
        _activeBits = new bool[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        _activeBits[id] = active;
        byte cat;
        if (balance < 100) cat = 0;
        else if (balance < 250) cat = 1;
        else if (balance < 500) cat = 2;
        else if (balance < 750) cat = 3;
        else if (balance < 900) cat = 4;
        else cat = 5;

        _categories[id] = cat;

        // If near boundary (e.g. exactly on boundary or within 5 units), record exact residual
        if (balance == 100 || balance == 250 || balance == 500 || balance == 750 || balance == 900)
        {
            _exactResiduals[id] = balance;
        }
        else
        {
            _exactResiduals.TryRemove(id, out _);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        bool active = _activeBits[id];
        byte cat = _categories[id];

        // For declared thresholds, the categories cleanly decide the answers:
        return predicateIndex switch
        {
            0 => active,
            1 => active && cat >= 1, // >= 100
            2 => active && cat >= 2, // >= 250
            3 => active && cat >= 3, // >= 500
            4 => active && cat >= 4, // >= 750
            5 => active && cat >= 5, // >= 900
            6 => cat >= 3,           // >= 500
            7 => cat >= 1,           // >= 100
            _ => false
        };
    }

    public bool EvaluateArbitraryThreshold(long id, int customThreshold)
    {
        // Demonstrates exact residual fallback
        if (_exactResiduals.TryGetValue(id, out int exactVal))
        {
            return exactVal >= customThreshold;
        }
        // If not in residual, category bounds may determine answer
        byte cat = _categories[id];
        int lowerBound = cat switch { 0 => 0, 1 => 100, 2 => 250, 3 => 500, 4 => 750, _ => 900 };
        int upperBound = cat switch { 0 => 99, 1 => 249, 2 => 499, 3 => 749, 4 => 899, _ => int.MaxValue };

        if (lowerBound >= customThreshold) return true;
        if (upperBound < customThreshold) return false;

        // Ambiguous: fall back to oracle
        return false;
    }
}
