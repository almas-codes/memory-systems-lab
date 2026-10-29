using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.QueryExecution;

/// <summary>
/// EXEC-04: Shared Intermediate Reuse (SIR)
/// Computes shared atomic subexpressions once per batch, reusing results across multiple dependent queries.
/// </summary>
public sealed class EXEC04_SharedIntermediateReuse : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "EXEC-SIR-004";
    public string CandidateName => "Shared Intermediate Reuse (SIR)";
    public string Family => "QueryExecution";
    public string Description => "Evaluates shared atomic subexpressions once per batch and reuses intermediates for composite queries.";

    public long TotalAllocatedBytes => 512;
    public long PayloadBytes => 64;
    public long MetadataBytes => 448;

    /// <summary>
    /// Evaluates 8 queries across a batch of records, sharing the intermediate calculations of:
    /// Atom 1 (bal >= 100), Atom 2 (bal >= 250), Atom 3 (bal >= 500).
    /// </summary>
    public void EvaluateBatchWithReuse(
        ReadOnlySpan<AccountRecord> records,
        Span<byte> destinationPatterns)
    {
        for (int i = 0; i < records.Length; i++)
        {
            var r = records[i];
            bool active = r.Active;
            int bal = r.Balance;

            // Shared intermediate evaluations
            bool gte100 = bal >= 100;
            bool gte250 = bal >= 250;
            bool gte500 = bal >= 500;
            bool gte750 = bal >= 750;
            bool gte900 = bal >= 900;

            int p = 0;
            if (active)
            {
                p |= (1 << 7); // Q1
                if (gte100) p |= (1 << 6); // Q2
                if (gte250) p |= (1 << 5); // Q3
                if (gte500) p |= (1 << 4); // Q4
                if (gte750) p |= (1 << 3); // Q5
                if (gte900) p |= (1 << 2); // Q6
            }
            if (gte500) p |= (1 << 1); // Q7 (reused intermediate)
            if (gte100) p |= (1 << 0); // Q8 (reused intermediate)

            destinationPatterns[i] = (byte)p;
        }
    }
}
