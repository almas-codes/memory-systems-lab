using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace NeuroCache.EapResearch.AdaptiveCompression;

public sealed class UpdateBenchmarkResult
{
    public string CandidateName { get; init; } = "";
    public long TotalUpdates { get; init; }
    public double TotalDurationMs { get; init; }
    public double UpdatesPerSec { get; init; }
    public double LatencyMeanNs { get; init; }
}

public sealed class SemanticCompressionUpdateRunner
{
    public static void VerifyCorrectnessAcrossAllCandidates(
        CandidateA_EightIndependentBitmaps candA,
        CandidateB_PackedByteAnswerPattern candB,
        CandidateC_SemanticAnswerClassPlane candC,
        long testAccounts = 100_000)
    {
        for (long id = 1; id <= testAccounts; id++)
        {
            byte patA = candA.QueryAll(id);
            byte patB = candB.QueryAll(id);
            byte patC = candC.QueryAll(id);

            if (patA != patB || patB != patC)
            {
                throw new InvalidOperationException(
                    $"Mismatch across candidates at ID {id}: CandA=0x{patA:X2}, CandB=0x{patB:X2}, CandC=0x{patC:X2}");
            }

            for (int q = 0; q < 8; q++)
            {
                bool a = candA.QuerySingle(id, q);
                bool b = candB.QuerySingle(id, q);
                bool c = candC.QuerySingle(id, q);

                if (a != b || b != c)
                {
                    throw new InvalidOperationException(
                        $"Mismatch on query Q{q + 1} at ID {id}: CandA={a}, CandB={b}, CandC={c}");
                }
            }
        }
    }

    public static void VerifyDynamicClassRegistration(CandidateC_SemanticAnswerClassPlane candC)
    {
        int initialCount = candC.DistinctClassCount;

        // Pattern 0xAA (10101010 in binary) - a synthetic alternating pattern
        byte syntheticPattern = 0xAA;
        byte classId = candC.GetOrRegisterPattern(syntheticPattern, "Synthetic Alternating Test Class");

        int newCount = candC.DistinctClassCount;
        if (newCount <= initialCount)
        {
            throw new InvalidOperationException("Failed to register new semantic class dynamically.");
        }

        long testId = 999_999;
        candC.SetExplicitPattern(testId, syntheticPattern);

        byte retrievedPattern = candC.QueryAll(testId);
        if (retrievedPattern != syntheticPattern)
        {
            throw new InvalidOperationException(
                $"Dynamic class query failed: expected 0x{syntheticPattern:X2}, got 0x{retrievedPattern:X2}");
        }

        // Verify individual bit extractions
        for (int q = 0; q < 8; q++)
        {
            bool expected = ((syntheticPattern >> (7 - q)) & 1) != 0;
            bool actual = candC.QuerySingle(testId, q);
            if (expected != actual)
            {
                throw new InvalidOperationException($"Dynamic class bit query mismatch on Q{q + 1}");
            }
        }
    }

    public static void VerifyAdjacentIdConcurrentUpdatesUnderCas(CandidateC_SemanticAnswerClassPlane candC)
    {
        // Account 200 (high nibble) and Account 201 (low nibble) share byte 100
        long idEven = 200;
        long idOdd = 201;

        const int iterations = 100_000;

        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    // Alternate between two patterns
                    byte pat = (i % 2 == 0) ? (byte)128 : (byte)193;
                    candC.SetExplicitPattern(idEven, pat);
                }
            },
            () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    byte pat = (i % 2 == 0) ? (byte)1 : (byte)0;
                    candC.SetExplicitPattern(idOdd, pat);
                }
            }
        );

        // Verify final state is valid and non-corrupted
        byte evenPat = candC.QueryAll(idEven);
        byte oddPat = candC.QueryAll(idOdd);

        if (evenPat != 128 && evenPat != 193)
        {
            throw new InvalidOperationException($"Corrupted even nibble at ID {idEven}: 0x{evenPat:X2}");
        }

        if (oddPat != 0 && oddPat != 1)
        {
            throw new InvalidOperationException($"Corrupted odd nibble at ID {idOdd}: 0x{oddPat:X2}");
        }
    }

    public static UpdateBenchmarkResult BenchmarkUpdatesCandidateA(CandidateA_EightIndependentBitmaps candA, int totalUpdates = 2_000_000)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < totalUpdates; i++)
        {
            long id = (i % candA.MaxId) + 1;
            int bal = (i % 1000);
            bool act = (i % 10) != 0;
            candA.Update(id, bal, act);
        }
        sw.Stop();

        return new UpdateBenchmarkResult
        {
            CandidateName = "Candidate A (8 Independent Bitmaps)",
            TotalUpdates = totalUpdates,
            TotalDurationMs = sw.Elapsed.TotalMilliseconds,
            UpdatesPerSec = totalUpdates / sw.Elapsed.TotalSeconds,
            LatencyMeanNs = sw.Elapsed.TotalNanoseconds / totalUpdates
        };
    }

    public static UpdateBenchmarkResult BenchmarkUpdatesCandidateB(CandidateB_PackedByteAnswerPattern candB, int totalUpdates = 2_000_000)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < totalUpdates; i++)
        {
            long id = (i % candB.MaxId) + 1;
            int bal = (i % 1000);
            bool act = (i % 10) != 0;
            candB.Update(id, bal, act);
        }
        sw.Stop();

        return new UpdateBenchmarkResult
        {
            CandidateName = "Candidate B (Packed 8-bit Pattern)",
            TotalUpdates = totalUpdates,
            TotalDurationMs = sw.Elapsed.TotalMilliseconds,
            UpdatesPerSec = totalUpdates / sw.Elapsed.TotalSeconds,
            LatencyMeanNs = sw.Elapsed.TotalNanoseconds / totalUpdates
        };
    }

    public static UpdateBenchmarkResult BenchmarkUpdatesCandidateC(CandidateC_SemanticAnswerClassPlane candC, int totalUpdates = 2_000_000)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < totalUpdates; i++)
        {
            long id = (i % candC.MaxId) + 1;
            int bal = (i % 1000);
            bool act = (i % 10) != 0;
            candC.Update(id, bal, act);
        }
        sw.Stop();

        return new UpdateBenchmarkResult
        {
            CandidateName = "Candidate C (Semantic Answer-Class)",
            TotalUpdates = totalUpdates,
            TotalDurationMs = sw.Elapsed.TotalMilliseconds,
            UpdatesPerSec = totalUpdates / sw.Elapsed.TotalSeconds,
            LatencyMeanNs = sw.Elapsed.TotalNanoseconds / totalUpdates
        };
    }
}
