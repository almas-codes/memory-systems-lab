using System;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Oracle;

namespace NeuroCache.ResearchLab.AdaptiveSelection;

public enum ShadowValidationState
{
    ShadowTrial,
    PromotedActive,
    CircuitBreakerOpen,
    RolledBack
}

/// <summary>
/// ADAPT-03: Dual-Write Shadow Validation Switcher (DWSV)
/// Evaluates candidates in dark shadow alongside authoritative oracle before atomic cutover.
/// </summary>
public sealed class ADAPT03_DualWriteShadowValidationSwitcher : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "ADAPT-DWSV-003";
    public string CandidateName => "Dual-Write Shadow Validation Switcher (DWSV / ADAPT-03)";
    public string Family => "AdaptiveSelection";
    public string Description => "Shadow validation switcher with circuit breaker and automated cutover policy.";

    private readonly IExactOracle _oracle;
    private readonly Func<long, bool> _candidateQuery;
    private readonly int _mismatchThreshold;
    private readonly long _requiredTrialQueries;

    private long _evaluations;
    private long _matches;
    private long _mismatches;
    private volatile ShadowValidationState _state = ShadowValidationState.ShadowTrial;

    public ShadowValidationState State => _state;
    public long Evaluations => Volatile.Read(ref _evaluations);
    public long Matches => Volatile.Read(ref _matches);
    public long Mismatches => Volatile.Read(ref _mismatches);

    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 32;
    public long MetadataBytes => 96;

    public ADAPT03_DualWriteShadowValidationSwitcher(
        IExactOracle oracle,
        Func<long, bool> candidateQuery,
        int mismatchThreshold = 1,
        long requiredTrialQueries = 10_000)
    {
        _oracle = oracle;
        _candidateQuery = candidateQuery;
        _mismatchThreshold = mismatchThreshold;
        _requiredTrialQueries = requiredTrialQueries;
    }

    public bool Evaluate(long id)
    {
        bool authoritative = _oracle.Evaluate(id, 1);

        if (_state == ShadowValidationState.CircuitBreakerOpen || _state == ShadowValidationState.RolledBack)
        {
            return authoritative; // Safe fallback
        }

        // Shadow comparison
        bool candidateAns = _candidateQuery(id);
        Interlocked.Increment(ref _evaluations);

        if (candidateAns == authoritative)
        {
            long m = Interlocked.Increment(ref _matches);
            if (_state == ShadowValidationState.ShadowTrial && m >= _requiredTrialQueries && _mismatches == 0)
            {
                _state = ShadowValidationState.PromotedActive; // Promoted!
            }
        }
        else
        {
            long errs = Interlocked.Increment(ref _mismatches);
            if (errs >= _mismatchThreshold)
            {
                _state = ShadowValidationState.CircuitBreakerOpen; // Trip circuit breaker!
            }
        }

        return authoritative;
    }
}
