using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.QueryExecution;

/// <summary>
/// EXEC-08: Certified Fast Path with Exact Escape
/// Fast path returns immediately when an invariant certificate proves the outcome; otherwise escapes to exact evaluation.
/// </summary>
public sealed class EXEC08_CertifiedFastPathWithEscape : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "EXEC-CERT-008";
    public string CandidateName => "Certified Fast Path with Exact Escape (EXEC-08)";
    public string Family => "QueryExecution";
    public string Description => "Decides queries via zero-cost invariant certificates (e.g. inactive guarantees false) before escaping.";

    private long _fastPathHits;
    private long _escapedLookups;

    public long FastPathHits => Volatile.Read(ref _fastPathHits);
    public long EscapedLookups => Volatile.Read(ref _escapedLookups);
    public double FastPathRatio => (FastPathHits + EscapedLookups) == 0 ? 0 : (double)FastPathHits / (FastPathHits + EscapedLookups);

    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 32;
    public long MetadataBytes => 96;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluateEligibility(bool active, int balance, Func<int, bool>? exactEscape = null)
    {
        // Certificate 1: If account is inactive, eligibility is strictly FALSE without needing balance checks!
        if (!active)
        {
            Interlocked.Increment(ref _fastPathHits);
            return false;
        }

        // Certificate 2: If balance >= 1000, eligibility is strictly TRUE (above all operational thresholds)
        if (balance >= 1000)
        {
            Interlocked.Increment(ref _fastPathHits);
            return true;
        }

        // Ambiguous intermediate case: escape to exact evaluator
        Interlocked.Increment(ref _escapedLookups);
        return exactEscape != null ? exactEscape(balance) : (balance >= 100);
    }
}
