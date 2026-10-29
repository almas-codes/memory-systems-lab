using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.QueryExecution;

/// <summary>
/// EXEC-05: Batch Vectorized Execution (BVE)
/// Evaluates numeric predicates across batches using SIMD hardware vectorization (Vector256/Vector128).
/// </summary>
public sealed class EXEC05_BatchVectorizedExecution : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "EXEC-BVE-005";
    public string CandidateName => "Batch Vectorized Execution (BVE)";
    public string Family => "QueryExecution";
    public string Description => "Processes 8 to 16 records per SIMD instruction using Vector256 integer comparisons.";

    public long TotalAllocatedBytes => 256;
    public long PayloadBytes => 64;
    public long MetadataBytes => 192;

    /// <summary>
    /// Vectorized evaluation of: balance >= threshold across a span of balances.
    /// </summary>
    public void EvaluateThresholdVectorized(
        ReadOnlySpan<int> balances,
        int threshold,
        Span<bool> results)
    {
        int i = 0;
        int vectorSize = Vector256<int>.Count; // 8 integers per vector
        var vThreshold = Vector256.Create(threshold);

        // Vectorized SIMD loop
        for (; i <= balances.Length - vectorSize; i += vectorSize)
        {
            var vBal = Vector256.Create(balances.Slice(i, vectorSize));
            var vCmp = Vector256.GreaterThanOrEqual(vBal, vThreshold);

            for (int k = 0; k < vectorSize; k++)
            {
                results[i + k] = vCmp[k] != 0;
            }
        }

        // Scalar remainder loop
        for (; i < balances.Length; i++)
        {
            results[i] = balances[i] >= threshold;
        }
    }
}
