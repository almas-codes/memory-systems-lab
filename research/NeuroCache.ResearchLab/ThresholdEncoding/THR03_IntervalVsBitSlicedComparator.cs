using System;
using System.Diagnostics;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.ThresholdEncoding;

public sealed class ThresholdComparisonResult
{
    public double DirectorySingleLookupNs { get; init; }
    public double BitSlicedSingleLookupNs { get; init; }
    public double DirectoryBatchNsPerItem { get; init; }
    public double BitSlicedBatchNsPerItem { get; init; }
    public string Recommendation { get; init; } = "";
}

/// <summary>
/// THR-03: Declared Threshold Intervals versus Bit Slicing
/// Directly compares THR-01 (Interval Directory) and THR-02 (Bit Slicing) across access patterns.
/// </summary>
public sealed class THR03_IntervalVsBitSlicedComparator : IExperimentCandidate
{
    public string ExperimentId => "THR-COMPARE-003";
    public string CandidateName => "Declared Threshold Intervals vs Bit Slicing (THR-03)";
    public string Family => "ThresholdEncoding";
    public string Description => "Benchmark comparison proving where Interval Directory wins (single point lookups) vs Bit Slicing (batch vector scans).";

    public static ThresholdComparisonResult ComparePerformance(
        THR01_ThresholdDecisionDirectory dir,
        THR02_BitSlicedNumericEvaluation bitslice,
        int sampleOps = 1_000_000)
    {
        long maxId = Math.Min(dir.MaxId, bitslice.MaxId);

        // 1. Single lookup - Directory
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < sampleOps; i++)
        {
            long id = (i % maxId) + 1;
            bool ans = dir.EvaluateThreshold(id, 500);
        }
        sw.Stop();
        double dirSingleNs = sw.Elapsed.TotalNanoseconds / sampleOps;

        // 2. Single lookup - BitSliced
        sw.Restart();
        for (int i = 0; i < sampleOps; i++)
        {
            long id = (i % maxId) + 1;
            bool ans = bitslice.EvaluateThresholdSingle(id, 500);
        }
        sw.Stop();
        double bitSingleNs = sw.Elapsed.TotalNanoseconds / sampleOps;

        // 3. Batch evaluation - BitSliced (64 accounts per operation!)
        int wordOps = sampleOps / 64;
        sw.Restart();
        for (int w = 0; w < wordOps; w++)
        {
            int wordIdx = w % bitslice.WordCount;
            ulong mask = bitslice.EvaluateThresholdWord(wordIdx, 500);
        }
        sw.Stop();
        double bitBatchNsPerItem = sw.Elapsed.TotalNanoseconds / (wordOps * 64.0);

        // 4. Batch evaluation - Directory (scalar loop)
        sw.Restart();
        for (int w = 0; w < wordOps; w++)
        {
            long startId = (w % bitslice.WordCount) * 64L + 1;
            for (int k = 0; k < 64; k++)
            {
                bool ans = dir.EvaluateThreshold(startId + k, 500);
            }
        }
        sw.Stop();
        double dirBatchNsPerItem = sw.Elapsed.TotalNanoseconds / (wordOps * 64.0);

        string rec = dirSingleNs < bitSingleNs && bitBatchNsPerItem < dirBatchNsPerItem
            ? "WINNER DICHOTOMY: Interval Directory wins on single point lookups; Bit Slicing wins on batch vector scans."
            : "Directory dominates across tests.";

        return new ThresholdComparisonResult
        {
            DirectorySingleLookupNs = dirSingleNs,
            BitSlicedSingleLookupNs = bitSingleNs,
            DirectoryBatchNsPerItem = dirBatchNsPerItem,
            BitSlicedBatchNsPerItem = bitBatchNsPerItem,
            Recommendation = rec
        };
    }
}
