using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.HybridEngine.Engines;

namespace NeuroCache.ResearchLab.ShadowPilot.Runners;

public sealed class ShadowAnalyticalBreakEvenRunner
{
    public sealed record BreakEvenDataPoint(
        int QueryHorizonK,
        long WriteEventsW,
        double HybridTotalCostMs,
        double PostgreSqlTotalCostMs,
        double CostDifferenceMs,
        double EffectiveSpeedup,
        bool HybridIsCheaper);

    public sealed record BreakEvenAnalysisResult(
        long DatasetRecords,
        double HydrationTimeMs,
        double MutationIngestTimeMsPer1000,
        double HybridQueryTimeMs,
        double PostgreSqlQueryTimeMs,
        int BreakEvenKZeroWrites,
        int BreakEvenK10kWrites,
        int BreakEvenK100kWrites,
        List<BreakEvenDataPoint> HorizonCurve);

    public static async Task<BreakEvenAnalysisResult> RunBreakEvenAnalysisAsync(string outputDir)
    {
        Console.WriteLine("----------------------------------------------------------------------");
        Console.WriteLine(" [PHASE H & I] AMORTIZED ANALYTICAL BREAK-EVEN & PREPARATION COST     ");
        Console.WriteLine("----------------------------------------------------------------------");

        const long recordCount = 1_000_000;
        var engine = new Thr02AnalyticalEngine(recordCount);

        // 1. Measure Hydration Time
        var swHydrate = Stopwatch.StartNew();
        for (int i = 1; i <= recordCount; i++)
        {
            int bal = (i % 500) * 20;
            bool act = (i % 10) != 0;
            engine.ApplyMutation(i, bal, act, 1);
        }
        swHydrate.Stop();
        double hydrationMs = swHydrate.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[Preparation Cost] Initial Hydration of {recordCount:N0} records: {hydrationMs:F2} ms");

        // 2. Measure CDC Mutation Ingestion Time (10,000 mutations)
        const int mutationBatch = 10_000;
        var swIngest = Stopwatch.StartNew();
        for (int i = 1; i <= mutationBatch; i++)
        {
            engine.ApplyMutation(i, (i % 400) * 25, true, i + 10);
        }
        swIngest.Stop();
        double ingestMsPer1000 = (swIngest.Elapsed.TotalMilliseconds / mutationBatch) * 1000.0;
        double ingestMsPerSingle = swIngest.Elapsed.TotalMilliseconds / mutationBatch;
        Console.WriteLine($"[Maintenance Cost] CDC Ingestion: {ingestMsPer1000:F3} ms per 1,000 mutations ({ingestMsPerSingle * 1000.0:F2} \u00b5s/mutation)");

        // 3. Measure Steady-State Query Times
        var query = new AnalyticalQuery(1000, true);
        const int queryReps = 200;
        var swQ = Stopwatch.StartNew();
        for (int i = 0; i < queryReps; i++)
        {
            _ = engine.CountMatching(query, recordCount);
        }
        swQ.Stop();
        double hybridQueryMs = swQ.Elapsed.TotalMilliseconds / queryReps;

        // PostgreSQL 1M table scan baseline (~132.5 ms)
        double pgQueryMs = 132.5;

        Console.WriteLine($"[Query Latency] Hybrid Bit-Sliced: {hybridQueryMs:F3} ms | PostgreSQL Table Scan: {pgQueryMs:F2} ms");

        // 4. Compute Break-Even Horizons K*
        // K* = (Hydration + W * Ingest) / (PgQuery - HybridQuery)
        double netSavingsPerQuery = pgQueryMs - hybridQueryMs;

        int kStarZeroWrites = (int)Math.Ceiling(hydrationMs / netSavingsPerQuery);
        int kStar10kWrites = (int)Math.Ceiling((hydrationMs + (10_000 * ingestMsPerSingle)) / netSavingsPerQuery);
        int kStar100kWrites = (int)Math.Ceiling((hydrationMs + (100_000 * ingestMsPerSingle)) / netSavingsPerQuery);

        Console.WriteLine($"[Break-Even K*] Zero Writes:       K* = {kStarZeroWrites} queries");
        Console.WriteLine($"[Break-Even K*] 10,000 Writes:     K* = {kStar10kWrites} queries");
        Console.WriteLine($"[Break-Even K*] 100,000 Writes:    K* = {kStar100kWrites} queries");

        // 5. Build Horizon Curve for K in [1 .. 100,000]
        int[] horizons = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1_000, 5_000, 10_000, 50_000, 100_000];
        var curve = new List<BreakEvenDataPoint>();

        Console.WriteLine();
        Console.WriteLine("| Query Horizon K | PostgreSQL Cost | Hybrid Total Cost | Difference (Savings) | Speedup Factor | Win? |");
        Console.WriteLine("|:---------------:|:---------------:|:-----------------:|:--------------------:|:--------------:|:----:|");

        foreach (int k in horizons)
        {
            // Assume 1,000 writes occurring over the query lifespan
            long writes = Math.Min(k * 10, 50_000);
            double pgCost = k * pgQueryMs;
            double hybridCost = hydrationMs + (writes * ingestMsPerSingle) + (k * hybridQueryMs);
            double diff = pgCost - hybridCost;
            double speedup = pgCost / hybridCost;
            bool win = hybridCost < pgCost;

            curve.Add(new BreakEvenDataPoint(k, writes, hybridCost, pgCost, diff, speedup, win));
            Console.WriteLine($"| {k,15:N0} | {pgCost,13:F1} ms | {hybridCost,15:F1} ms | {diff,18:F1} ms | {speedup,12:F2}x | {(win ? "HYBRID" : "PG"),4} |");
        }

        var result = new BreakEvenAnalysisResult(
            recordCount,
            hydrationMs,
            ingestMsPer1000,
            hybridQueryMs,
            pgQueryMs,
            kStarZeroWrites,
            kStar10kWrites,
            kStar100kWrites,
            curve);

        string jsonPath = Path.Combine(outputDir, "Raw", "BreakEvenAnalysisResult.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        return result;
    }
}
