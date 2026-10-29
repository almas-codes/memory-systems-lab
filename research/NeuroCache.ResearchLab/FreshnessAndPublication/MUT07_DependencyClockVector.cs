using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.FreshnessAndPublication;

/// <summary>
/// MUT-07: Dependency Clock Vector per Domain (DCV)
/// Tracks monotonic change versions across domain groups (Domain 0: Balance, Domain 1: Status).
/// Invalidates only queries dependent on changed domains.
/// </summary>
public sealed class MUT07_DependencyClockVector : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "FRESH-DCV-007";
    public string CandidateName => "Dependency Clock Vector per Domain (DCV)";
    public string Family => "FreshnessAndPublication";
    public string Description => "Tracks independent version vectors per domain; isolates query invalidations to affected domains.";

    private long _balanceDomainVersion = 1;
    private long _statusDomainVersion = 1;

    public long BalanceDomainVersion => Volatile.Read(ref _balanceDomainVersion);
    public long StatusDomainVersion => Volatile.Read(ref _statusDomainVersion);

    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 16;
    public long MetadataBytes => 112;

    public void RecordBalanceChange() => Interlocked.Increment(ref _balanceDomainVersion);
    public void RecordStatusChange() => Interlocked.Increment(ref _statusDomainVersion);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsQueryValid(int queryIndex, long cachedBalanceVer, long cachedStatusVer)
    {
        // Query dependencies:
        // Q1 (active): depends only on status
        // Q7 (bal >= 500), Q8 (bal >= 100): depend only on balance
        // Q2..Q6: depend on both
        return queryIndex switch
        {
            0 => cachedStatusVer == StatusDomainVersion,
            6 or 7 => cachedBalanceVer == BalanceDomainVersion,
            _ => (cachedStatusVer == StatusDomainVersion) && (cachedBalanceVer == BalanceDomainVersion)
        };
    }
}
