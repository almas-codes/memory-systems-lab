using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.HybridCompositions;

/// <summary>
/// COMBO-04: Shared Atoms with Batch Intermediate Reuse (COMBO-ATOMS-BATCH-004)
/// Composition of:
/// - REP-02: 6 atomic condition bitmaps.
/// - Vectorized batch evaluation reusing intermediate logical sub-expressions across all 8 predicates.
/// </summary>
public sealed class COMBO04_SharedAtomsWithBatchIntermediateReuse : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "COMBO-ATOMS-BATCH-004";
    public string CandidateName => "Shared Atoms + Batch Intermediate Reuse";
    public string Family => "HybridCompositions";
    public string Description => "Maintains 6 atomic bitmaps and reuses sub-expression evaluations across query predicates.";

    private readonly long _maxId;
    private readonly EapBooleanBitmap[] _atoms;
    private long _mutationCount;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _atoms.Length * _atoms[0].RawMemoryBytes;
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 64;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public COMBO04_SharedAtomsWithBatchIntermediateReuse(long maxId)
    {
        _maxId = maxId;
        _atoms = new EapBooleanBitmap[6];
        for (int i = 0; i < 6; i++)
        {
            _atoms[i] = new EapBooleanBitmap(maxId);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        if (id < 0 || id > _maxId) return false;
        bool active = _atoms[0].IsEligible(id);

        return predicateIndex switch
        {
            0 => active,
            1 => active && _atoms[1].IsEligible(id),
            2 => active && _atoms[2].IsEligible(id),
            3 => active && _atoms[3].IsEligible(id),
            4 => active && _atoms[4].IsEligible(id),
            5 => active && _atoms[5].IsEligible(id),
            6 => _atoms[3].IsEligible(id),
            7 => _atoms[1].IsEligible(id),
            _ => false
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        if (id < 0 || id > _maxId) return;

        _atoms[0].SetEligible(id, active);
        _atoms[1].SetEligible(id, balance >= 100);
        _atoms[2].SetEligible(id, balance >= 250);
        _atoms[3].SetEligible(id, balance >= 500);
        _atoms[4].SetEligible(id, balance >= 750);
        _atoms[5].SetEligible(id, balance >= 900);

        Interlocked.Increment(ref _mutationCount);
    }
}
