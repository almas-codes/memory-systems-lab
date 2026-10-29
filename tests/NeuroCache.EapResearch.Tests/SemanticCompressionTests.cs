using System;
using System.Threading.Tasks;
using NeuroCache.EapResearch.AdaptiveCompression;
using Xunit;

namespace NeuroCache.EapResearch.Tests;

public sealed class SemanticCompressionTests
{
    [Fact]
    public void VerifyQueryFamily_PredicateEvaluationAndPatternExtraction()
    {
        // Case 1: Inactive account with low balance
        byte p1 = QueryFamily.ComputeAnswerPattern(balance: 50, active: false);
        Assert.Equal(0, p1);
        for (int q = 0; q < 8; q++)
        {
            Assert.False(QueryFamily.ExtractAnswer(p1, q));
            Assert.False(QueryFamily.EvaluatePredicate(q, balance: 50, active: false));
        }

        // Case 2: Inactive account with high balance (balance >= 500)
        // Q7 (bit 1) and Q8 (bit 0) should be true even when inactive!
        byte p2 = QueryFamily.ComputeAnswerPattern(balance: 600, active: false);
        Assert.Equal(3, p2); // 00000011b
        Assert.False(QueryFamily.ExtractAnswer(p2, 0)); // Q1_Active
        Assert.False(QueryFamily.ExtractAnswer(p2, 3)); // Q4_ActiveAndBalGte500
        Assert.True(QueryFamily.ExtractAnswer(p2, 6));  // Q7_BalGte500
        Assert.True(QueryFamily.ExtractAnswer(p2, 7));  // Q8_BalGte100

        // Case 3: Active account with balance 950 (All queries true)
        byte p3 = QueryFamily.ComputeAnswerPattern(balance: 950, active: true);
        Assert.Equal(0xFF, p3); // 11111111b
        for (int q = 0; q < 8; q++)
        {
            Assert.True(QueryFamily.ExtractAnswer(p3, q));
            Assert.True(QueryFamily.EvaluatePredicate(q, balance: 950, active: true));
        }
    }

    [Fact]
    public void VerifyCandidates_EquivalenceAcrossAllEightQueries()
    {
        const int recordCount = 5_000;
        var candA = new CandidateA_EightIndependentBitmaps(recordCount);
        var candB = new CandidateB_PackedByteAnswerPattern(recordCount);
        var candC = new CandidateC_SemanticAnswerClassPlane(recordCount);

        // Populate with synthetic varying records
        for (long id = 1; id <= recordCount; id++)
        {
            int bal = (int)(id * 17 % 1000);
            bool act = (id % 3) != 0;

            candA.Update(id, bal, act);
            candB.Update(id, bal, act);
            candC.Update(id, bal, act);
        }

        // Verify exact equivalence across all queries and patterns
        for (long id = 1; id <= recordCount; id++)
        {
            byte patA = candA.QueryAll(id);
            byte patB = candB.QueryAll(id);
            byte patC = candC.QueryAll(id);

            Assert.Equal(patA, patB);
            Assert.Equal(patB, patC);

            for (int q = 0; q < 8; q++)
            {
                bool a = candA.QuerySingle(id, q);
                bool b = candB.QuerySingle(id, q);
                bool c = candC.QuerySingle(id, q);

                Assert.Equal(a, b);
                Assert.Equal(b, c);
            }
        }
    }

    [Fact]
    public void VerifyCandidateC_DynamicClassRegistration_DiscoversNovelPatterns()
    {
        var candC = new CandidateC_SemanticAnswerClassPlane(100);
        int initialCount = candC.DistinctClassCount;

        byte novelPattern = 0x55; // 01010101b
        byte classId = candC.GetOrRegisterPattern(novelPattern, "Novel alternating pattern");

        Assert.Equal(initialCount + 1, candC.DistinctClassCount);
        Assert.True(classId < 16);

        candC.SetExplicitPattern(42, novelPattern);

        Assert.Equal(novelPattern, candC.QueryAll(42));
        Assert.Equal(classId, candC.QueryClassId(42));

        for (int q = 0; q < 8; q++)
        {
            bool expected = ((novelPattern >> (7 - q)) & 1) != 0;
            Assert.Equal(expected, candC.QuerySingle(42, q));
        }
    }

    [Fact]
    public void VerifyCandidateC_AdjacentIdConcurrency_NoTornNibblesUnderCas()
    {
        var candC = new CandidateC_SemanticAnswerClassPlane(1_000);

        long evenId = 500;
        long oddId = 501;

        const int iterations = 50_000;

        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    byte p = (i % 2 == 0) ? (byte)128 : (byte)193;
                    candC.SetExplicitPattern(evenId, p);
                }
            },
            () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    byte p = (i % 2 == 0) ? (byte)1 : (byte)0;
                    candC.SetExplicitPattern(oddId, p);
                }
            }
        );

        byte evenResult = candC.QueryAll(evenId);
        byte oddResult = candC.QueryAll(oddId);

        Assert.True(evenResult == 128 || evenResult == 193, $"Invalid even result: {evenResult}");
        Assert.True(oddResult == 0 || oddResult == 1, $"Invalid odd result: {oddResult}");
    }

    [Fact]
    public void VerifyMemoryFootprints_DecimalVsBinary_AndFiftyPercentAdvantage()
    {
        const long fiftyMillion = 50_000_000;
        var candA = new CandidateA_EightIndependentBitmaps(fiftyMillion);
        var candB = new CandidateB_PackedByteAnswerPattern(fiftyMillion);
        var candC = new CandidateC_SemanticAnswerClassPlane(fiftyMillion);
        var candD = new CandidateD_ReferenceSingleBitmap(fiftyMillion);

        // Candidate A: 8 bitmaps * 6,250,000 bytes = 50,000,000 bytes
        Assert.Equal(50_000_000L, candA.TotalAllocatedBytes);
        double mbA = candA.TotalAllocatedBytes / 1_000_000.0;
        double mibA = candA.TotalAllocatedBytes / (1024.0 * 1024.0);
        Assert.Equal(50.0, mbA);
        Assert.InRange(mibA, 47.68, 47.69);

        // Candidate B: 50,000,001 bytes ~ 50.0 MB
        Assert.InRange(candB.TotalAllocatedBytes, 50_000_000L, 50_000_001L);

        // Candidate C: 25,000,001 bytes + 272 dictionary bytes ~ 25.0 MB
        Assert.InRange(candC.TotalAllocatedBytes, 25_000_000L, 25_001_000L);
        double mbC = candC.TotalAllocatedBytes / 1_000_000.0;
        double mibC = candC.TotalAllocatedBytes / (1024.0 * 1024.0);
        Assert.InRange(mbC, 25.0, 25.01);
        Assert.InRange(mibC, 23.84, 23.85);

        // Candidate D: 6,250,000 bytes = 6.25 MB decimal (5.96 MiB binary)
        Assert.Equal(6_250_000L, candD.TotalAllocatedBytes);
        double mbD = candD.TotalAllocatedBytes / 1_000_000.0;
        double mibD = candD.TotalAllocatedBytes / (1024.0 * 1024.0);
        Assert.Equal(6.25, mbD);
        Assert.InRange(mibD, 5.96, 5.97);

        // Memory Advantage: Candidate C is ~50% the size of Candidate A and Candidate B
        double reduction = 1.0 - ((double)candC.TotalAllocatedBytes / candA.TotalAllocatedBytes);
        Assert.InRange(reduction, 0.499, 0.501); // 50% memory reduction!
    }
}
