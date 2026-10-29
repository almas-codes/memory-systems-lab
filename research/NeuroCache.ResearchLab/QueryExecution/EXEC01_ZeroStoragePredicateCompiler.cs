using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.QueryExecution;

/// <summary>
/// EXEC-01: Zero-Storage Predicate Compiler (ZSPC)
/// Never materializes precomputed answers; computes predicates directly on-the-fly over source records.
/// </summary>
public sealed class EXEC01_ZeroStoragePredicateCompiler : IExperimentCandidate, IPredicateEvaluator<long>, IMemoryAccountable
{
    public string ExperimentId => "EXEC-ZSPC-001";
    public string CandidateName => "Zero-Storage Predicate Compiler (ZSPC)";
    public string Family => "QueryExecution";
    public string Description => "Zero materialized answer storage; compiles predicates directly to on-the-fly record evaluation.";

    private readonly Func<long, AccountRecord?> _recordSupplier;

    public long TotalAllocatedBytes => 0; // Pure compute, zero materialized answer bytes!
    public long PayloadBytes => 0;
    public long MetadataBytes => 64;

    public EXEC01_ZeroStoragePredicateCompiler(Func<long, AccountRecord?> recordSupplier)
    {
        _recordSupplier = recordSupplier;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        var record = _recordSupplier(id);
        if (record == null) return false;
        return record.Value.EvaluatePredicate(predicateIndex);
    }
}
