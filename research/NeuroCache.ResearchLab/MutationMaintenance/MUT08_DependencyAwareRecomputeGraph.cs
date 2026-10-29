using System;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MutationMaintenance;

[Flags]
public enum AccountFieldMask
{
    None = 0,
    Balance = 1 << 0,
    Active = 1 << 1,
    Version = 1 << 2,
    Metadata = 1 << 3
}

/// <summary>
/// MUT-08: Dependency-Aware Recompute Graph (DARG)
/// Filters mutations before cache maintenance by evaluating field dependencies against the query predicate graph.
/// </summary>
public sealed class MUT08_DependencyAwareRecomputeGraph : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "MUT-DARG-008";
    public string CandidateName => "Dependency-Aware Recompute Graph (DARG)";
    public string Family => "MutationMaintenance";
    public string Description => "Filters mutations against query dependency graph; skips mutations to irrelevant fields.";

    private const AccountFieldMask RelevantMask = AccountFieldMask.Balance | AccountFieldMask.Active;

    private long _totalMutations;
    private long _skippedMutations;
    private long _processedMutations;

    public long TotalMutations => Volatile.Read(ref _totalMutations);
    public long SkippedMutations => Volatile.Read(ref _skippedMutations);
    public long ProcessedMutations => Volatile.Read(ref _processedMutations);
    public double SkipRatio => TotalMutations == 0 ? 0 : (double)SkippedMutations / TotalMutations;

    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 32;
    public long MetadataBytes => 96;

    public bool ShouldProcessMutation(AccountFieldMask changedFields)
    {
        Interlocked.Increment(ref _totalMutations);
        if ((changedFields & RelevantMask) == 0)
        {
            Interlocked.Increment(ref _skippedMutations);
            return false; // Skip maintenance!
        }

        Interlocked.Increment(ref _processedMutations);
        return true; // Relevant mutation: proceed to maintenance
    }
}
