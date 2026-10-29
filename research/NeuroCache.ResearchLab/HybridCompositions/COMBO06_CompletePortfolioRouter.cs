using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Recovery;

namespace NeuroCache.ResearchLab.HybridCompositions;

/// <summary>
/// COMBO-06: Complete Portfolio Router (COMBO-PORTFOLIO-006)
/// An integrated portfolio combining:
/// - COMBO-02 (4-bit semantic equivalence classes) for point lookups.
/// - REC-02 (Checkpointed Contiguous Prefix Replay) for transaction-consistent recovery.
/// </summary>
public sealed class COMBO06_CompletePortfolioRouter : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "COMBO-PORTFOLIO-006";
    public string CandidateName => "Complete Portfolio Router (COMBO-06)";
    public string Family => "HybridCompositions";
    public string Description => "Routes random point lookups to 4-bit semantic classes and mutations to checkpointed recovery engine.";

    private readonly long _maxId;
    private readonly COMBO02_SemanticClassesWithRareExceptions _lookupEngine;
    private readonly REC02_CheckpointedContiguousPrefixReplay _recoveryEngine;
    private long _mutationCount;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _lookupEngine.TotalAllocatedBytes + _recoveryEngine.TotalAllocatedBytes;
    public long PayloadBytes => _lookupEngine.PayloadBytes + _recoveryEngine.PayloadBytes;
    public long MetadataBytes => _lookupEngine.MetadataBytes + _recoveryEngine.MetadataBytes;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public COMBO06_CompletePortfolioRouter(long maxId)
    {
        _maxId = maxId;
        _lookupEngine = new COMBO02_SemanticClassesWithRareExceptions(maxId);
        _recoveryEngine = new REC02_CheckpointedContiguousPrefixReplay(maxId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        return _lookupEngine.EvaluatePredicate(id, predicateIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        return _lookupEngine.QueryAll(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        _lookupEngine.HandleMutation(id, balance, active, sequenceId);
        _recoveryEngine.HandleMutation(id, balance, active, sequenceId);
        Interlocked.Increment(ref _mutationCount);
    }
}
