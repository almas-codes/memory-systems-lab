using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Representations;

namespace NeuroCache.ResearchLab.HybridCompositions;

/// <summary>
/// COMBO-01: Factorized Atoms with DARG and Epoch Freshness (COMBO-FSCG-DARG-001)
/// Composition of:
/// - REP-02 (Factorized Shared Condition Graph): 6 atomic condition bitmaps.
/// - Dependency-aware recomputation.
/// - 64-bit monotonic epoch tracking for publication consistency.
/// </summary>
public sealed class COMBO01_FactorizedAtomsWithDargAndFreshness : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "COMBO-FSCG-DARG-001";
    public string CandidateName => "Factorized Atoms + DARG + Epoch Freshness";
    public string Family => "HybridCompositions";
    public string Description => "Combines factorized shared condition graph (REP-02) with 64-bit monotonic epoch publication.";

    private readonly REP02_FactorizedSharedConditionGraph _fscg;
    private readonly long _maxId;
    private ulong _epoch = 1;
    private long _mutationCount;

    public long MaxId => _maxId;
    public ulong CurrentEpoch => Volatile.Read(ref _epoch);
    public long TotalAllocatedBytes => _fscg.TotalAllocatedBytes + 64;
    public long PayloadBytes => _fscg.PayloadBytes;
    public long MetadataBytes => _fscg.MetadataBytes + 64;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public COMBO01_FactorizedAtomsWithDargAndFreshness(long maxId)
    {
        _maxId = maxId;
        _fscg = new REP02_FactorizedSharedConditionGraph(maxId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        return _fscg.EvaluatePredicate(id, predicateIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        _fscg.HandleMutation(id, balance, active, sequenceId);
        Interlocked.Increment(ref _epoch);
        Interlocked.Increment(ref _mutationCount);
    }
}
