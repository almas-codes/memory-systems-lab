using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.HybridCompositions;
using NeuroCache.ResearchLab.Recovery;
using NeuroCache.ResearchLab.Representations;
using NeuroCache.ResearchLab.SemanticEncoding;

namespace NeuroCache.ResearchLab.Data;

public sealed class FairTournament50MRunner
{
    public sealed record LatencyStats(
        double P50Ns,
        double P95Ns,
        double P99Ns,
        double MaxNs);

    public sealed record Candidate50MResult(
        string CandidateId,
        string CandidateName,
        string Family,
        long AccountCount,
        long PayloadBytes,
        long MetadataBytes,
        long TotalAllocatedBytes,
        double DecimalMb,
        double BinaryMib,
        double BitsPerAccount,
        double ConstructionDurationSeconds,
        long InitialWorkingSetMb,
        long FinalWorkingSetMb,
        int GcGen0Collections,
        int GcGen1Collections,
        int GcGen2Collections,
        // Uniform Random Read
        double SingleThreadReadMops,
        LatencyStats SingleThreadReadLatency,
        double MultiThread8ReadMops,
        LatencyStats MultiThread8ReadLatency,
        double MultiThread16ReadMops,
        LatencyStats MultiThread16ReadLatency,
        // Zipfian Skewed Read (80/20)
        double Zipfian8ReadMops,
        double Zipfian16ReadMops,
        // Sequential Scan Read
        double SequentialScanMops,
        double SequentialScanDurationSeconds,
        // Writes
        double SingleThreadWriteMops,
        double MultiThread8WriteMops,
        double AdjacentContentionWriteMops,
        // Mixed Concurrency
        double Mixed99Read1WriteMops,
        double Mixed95Read5WriteMops);

    public static async Task<List<Candidate50MResult>> RunTournament50MAsync(
        long accountCount = 50_000_000,
        string? resultsDir = null)
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine("   NEUROCACHE RESEARCH LAB: 50,000,000 RECORD CANDIDATE TOURNAMENT       ");
        Console.WriteLine($"   Target Records: {accountCount:N0} | CPU: AMD Ryzen 7 5825U (8C/16T)  ");
        Console.WriteLine("==========================================================================");

        var candidateDefs = new (string Id, string Name, string Family, Func<object> Factory)[]
        {
            ("SEM-CLASS3-004", "Three-Bit Semantic Classes (SEM-04)", "SemanticEncoding", () => new SEM04_ThreeBitSemanticClasses(accountCount)),
            ("SEM-CLASS4-003", "Four-Bit Semantic Classes (SEM-03)", "SemanticEncoding", () => new SEM03_FourBitSemanticClasses(accountCount)),
            ("COMBO-SEM-SPARSE-002", "Semantic Classes with Rare Exceptions (COMBO-02)", "HybridCompositions", () => new COMBO02_SemanticClassesWithRareExceptions(accountCount)),
            ("SEM-BYTEVEC-002", "Byte-per-Record Answer Vector (SEM-02)", "SemanticEncoding", () => new SEM02_ByteAnswerVector(accountCount)),
            ("REP-CJB-001", "Correlated-Outcome Joint Bitmap (CJB)", "Representations", () => new REP01_CorrelatedOutcomeJointBitmap(accountCount)),
            ("SEM-BITMAPS-001", "Independent Bitmaps per Predicate (SEM-01)", "SemanticEncoding", () => new SEM01_IndependentBitmaps(accountCount)),
            ("REC-SEQLOCK-003", "Seqlock Page Publication (REC-03)", "Recovery", () => new REC03_SeqlockPagePublication(accountCount))
        };

        var results = new List<Candidate50MResult>();

        foreach (var def in candidateDefs)
        {
            Console.WriteLine();
            Console.WriteLine($"--------------------------------------------------------------------------");
            Console.WriteLine($" >>> Benchmarking Candidate: {def.Id} - {def.Name}");
            Console.WriteLine($"--------------------------------------------------------------------------");

            // Aggressive GC before candidate initialization
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(200);

            long workingSetStart = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
            int gc0Start = GC.CollectionCount(0);
            int gc1Start = GC.CollectionCount(1);
            int gc2Start = GC.CollectionCount(2);

            // 1. Initialization & 50M Population
            var initSw = Stopwatch.StartNew();
            object instance = def.Factory();
            var evaluator = (IPredicateEvaluator<long>)instance;
            var mutator = (ISourceMutationHandler)instance;
            var accountable = (IMemoryAccountable)instance;

            Console.Write($"Populating {accountCount:N0} records with 50M production distribution... ");

            // Populate according to the verified 8-pattern distribution:
            // 45% -> 128 (bal 50, act true)
            // 45% -> 193 (bal 150, act true)
            // 5%  -> 0   (bal 50, act false)
            // 5%  -> 1   (bal 150, act false)
            // Plus 113 rare exception records
            const int batchSize = 1_000_000;
            for (long baseId = 1; baseId <= accountCount; baseId += batchSize)
            {
                long endId = Math.Min(baseId + batchSize - 1, accountCount);
                for (long id = baseId; id <= endId; id++)
                {
                    int patternKey = (int)(id % 100);
                    int bal;
                    bool act;
                    if (patternKey < 45) { act = true; bal = 50; }      // 128
                    else if (patternKey < 90) { act = true; bal = 150; } // 193
                    else if (patternKey < 95) { act = false; bal = 50; } // 0
                    else { act = false; bal = 150; }                    // 1

                    mutator.HandleMutation(id, bal, act, 1);
                }
            }

            // Inject 113 rare exception patterns
            for (int r = 0; r < 100; r++) mutator.HandleMutation(500_000L + r * 10_000L, 1050, true, 1);  // Pattern 251
            for (int r = 0; r < 5; r++) mutator.HandleMutation(1_500_000L + r * 20_000L, 720, true, 1);   // Pattern 243
            for (int r = 0; r < 5; r++) mutator.HandleMutation(2_500_000L + r * 20_000L, 10000, true, 1); // Pattern 255
            for (int r = 0; r < 3; r++) mutator.HandleMutation(3_500_000L + r * 30_000L, 500, false, 1);  // Pattern 3

            initSw.Stop();
            double initDuration = initSw.Elapsed.TotalSeconds;

            long payloadBytes = accountable.PayloadBytes;
            long metadataBytes = accountable.MetadataBytes;
            long totalBytes = accountable.TotalAllocatedBytes;
            double decimalMb = totalBytes / 1_000_000.0;
            double binaryMib = totalBytes / (1024.0 * 1024.0);
            double bitsPerAcc = (totalBytes * 8.0) / accountCount;

            long workingSetAfterInit = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
            Console.WriteLine($"Done ({initDuration:F2}s).");
            Console.WriteLine($"  Memory: {decimalMb:F2} MB decimal ({binaryMib:F2} MiB) | {bitsPerAcc:F2} bits/account");
            Console.WriteLine($"  Payload: {payloadBytes:N0} B | Metadata: {metadataBytes:N0} B | Working Set: {workingSetAfterInit} MB");

            // Warmup (100k queries)
            for (int w = 1; w <= 100_000; w++)
            {
                _ = evaluator.EvaluatePredicate(w, w & 7);
            }

            // 2. Single-Thread Uniform Random Reads (2,000,000 ops)
            Console.Write("  Measuring Single-Thread Read... ");
            const int stReadOps = 2_000_000;
            var latenciesSampled = new List<double>(20_000);
            double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < stReadOps; i++)
            {
                long accId = 1 + ((i * 104729L) % accountCount);
                int pred = i & 7;

                if ((i & 127) == 0 && latenciesSampled.Count < 20_000)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    _ = evaluator.EvaluatePredicate(accId, pred);
                    long t1 = Stopwatch.GetTimestamp();
                    latenciesSampled.Add((t1 - t0) * nsPerTick);
                }
                else
                {
                    _ = evaluator.EvaluatePredicate(accId, pred);
                }
            }
            sw.Stop();
            double stReadMops = (stReadOps / sw.Elapsed.TotalSeconds) / 1_000_000.0;
            var stLatency = ComputeLatencyStats(latenciesSampled);
            Console.WriteLine($"{stReadMops:F2} Mops/s (p50: {stLatency.P50Ns:F1}ns, p95: {stLatency.P95Ns:F1}ns, p99: {stLatency.P99Ns:F1}ns)");

            // 3. Multi-Thread Uniform Random Reads (8 Threads, 2M ops/thread = 16M total ops)
            Console.Write("  Measuring Multi-Thread 8 Read... ");
            var mt8Result = await RunMultiThreadReadAsync(evaluator, accountCount, threads: 8, opsPerThread: 2_000_000);
            Console.WriteLine($"{mt8Result.Mops:F2} Mops/s (p50: {mt8Result.Latency.P50Ns:F1}ns, p95: {mt8Result.Latency.P95Ns:F1}ns, p99: {mt8Result.Latency.P99Ns:F1}ns)");

            // 4. Multi-Thread Uniform Random Reads (16 Threads, 1M ops/thread = 16M total ops)
            Console.Write("  Measuring Multi-Thread 16 Read... ");
            var mt16Result = await RunMultiThreadReadAsync(evaluator, accountCount, threads: 16, opsPerThread: 1_000_000);
            Console.WriteLine($"{mt16Result.Mops:F2} Mops/s (p50: {mt16Result.Latency.P50Ns:F1}ns, p95: {mt16Result.Latency.P95Ns:F1}ns, p99: {mt16Result.Latency.P99Ns:F1}ns)");

            // 5. Zipfian Skewed Read (80/20 rule: 80% traffic hits top 1% hot accounts = 500,000 accounts)
            Console.Write("  Measuring Zipfian Read (80/20)... ");
            double zipf8Mops = await RunZipfianReadAsync(evaluator, accountCount, threads: 8, opsPerThread: 2_000_000);
            double zipf16Mops = await RunZipfianReadAsync(evaluator, accountCount, threads: 16, opsPerThread: 1_000_000);
            Console.WriteLine($"8T: {zipf8Mops:F2} Mops/s | 16T: {zipf16Mops:F2} Mops/s");

            // 6. Sequential Scan Read (Scan all 50M accounts)
            Console.Write("  Measuring Sequential 50M Scan... ");
            sw.Restart();
            long scanSum = 0;
            for (long id = 1; id <= accountCount; id++)
            {
                if (evaluator.EvaluatePredicate(id, 0)) scanSum++;
            }
            sw.Stop();
            double scanDuration = sw.Elapsed.TotalSeconds;
            double scanMops = (accountCount / scanDuration) / 1_000_000.0;
            Console.WriteLine($"{scanMops:F2} Mops/s ({scanDuration:F2}s, matched: {scanSum:N0})");

            // 7. Single-Thread Writes (1,000,000 random mutations)
            Console.Write("  Measuring Single-Thread Write... ");
            const int stWriteOps = 1_000_000;
            sw.Restart();
            for (int i = 0; i < stWriteOps; i++)
            {
                long accId = 1 + ((i * 104729L) % accountCount);
                mutator.HandleMutation(accId, (i & 1) == 0 ? 50 : 150, (i & 2) == 0, 100 + i);
            }
            sw.Stop();
            double stWriteMops = (stWriteOps / sw.Elapsed.TotalSeconds) / 1_000_000.0;
            Console.WriteLine($"{stWriteMops:F2} Mops/s");

            // 8. Multi-Thread Writes (8 Threads, 250,000 ops/thread = 2,000,000 total ops)
            Console.Write("  Measuring Multi-Thread 8 Write... ");
            sw.Restart();
            var writeTasks = new Task[8];
            for (int t = 0; t < 8; t++)
            {
                int threadId = t;
                writeTasks[t] = Task.Run(() =>
                {
                    var rand = new Random(5000 + threadId);
                    for (int i = 0; i < 250_000; i++)
                    {
                        long accId = 1 + rand.Next(0, (int)accountCount);
                        mutator.HandleMutation(accId, (i & 1) == 0 ? 50 : 150, (i & 2) == 0, 200 + i);
                    }
                });
            }
            await Task.WhenAll(writeTasks);
            sw.Stop();
            double mt8WriteMops = (2_000_000.0 / sw.Elapsed.TotalSeconds) / 1_000_000.0;
            Console.WriteLine($"{mt8WriteMops:F2} Mops/s");

            // 9. Multi-Thread Adjacent Contention Writes (8 Threads sharing nibbles/bytes)
            Console.Write("  Measuring Adjacent-ID Contention Write... ");
            sw.Restart();
            var contentionTasks = new Task[8];
            for (int t = 0; t < 8; t++)
            {
                int pair = t / 2;
                bool isOdd = (t & 1) != 0;
                contentionTasks[t] = Task.Run(() =>
                {
                    // Contention block: pairs of threads write to adjacent even/odd IDs in a 1,000,000 range
                    for (int i = 0; i < 200_000; i++)
                    {
                        long baseAcc = (pair * 250_000L + (i % 250_000)) * 2L;
                        long accId = isOdd ? baseAcc + 1 : baseAcc;
                        mutator.HandleMutation(accId, (i & 1) == 0 ? 50 : 150, (i & 2) == 0, 300 + i);
                    }
                });
            }
            await Task.WhenAll(contentionTasks);
            sw.Stop();
            double adjContentionWriteMops = (1_600_000.0 / sw.Elapsed.TotalSeconds) / 1_000_000.0;
            Console.WriteLine($"{adjContentionWriteMops:F2} Mops/s");

            // 10. Mixed 99% Read / 1% Write (8 Threads: 7 Readers, 1 Writer)
            Console.Write("  Measuring Mixed 99/1 Concurrency... ");
            double mixed99Mops = await RunMixedWorkloadAsync(evaluator, mutator, accountCount, readerThreads: 7, writerThreads: 1, durationSeconds: 2.0);
            Console.WriteLine($"{mixed99Mops:F2} Mops/s");

            // 11. Mixed 95% Read / 5% Write (8 Threads: 6 Readers, 2 Writers)
            Console.Write("  Measuring Mixed 95/5 Concurrency... ");
            double mixed95Mops = await RunMixedWorkloadAsync(evaluator, mutator, accountCount, readerThreads: 6, writerThreads: 2, durationSeconds: 2.0);
            Console.WriteLine($"{mixed95Mops:F2} Mops/s");

            // Cleanup & End Metrics
            int gc0End = GC.CollectionCount(0) - gc0Start;
            int gc1End = GC.CollectionCount(1) - gc1Start;
            int gc2End = GC.CollectionCount(2) - gc2Start;
            long workingSetEnd = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

            var candResult = new Candidate50MResult(
                def.Id,
                def.Name,
                def.Family,
                accountCount,
                payloadBytes,
                metadataBytes,
                totalBytes,
                decimalMb,
                binaryMib,
                bitsPerAcc,
                initDuration,
                workingSetStart,
                workingSetEnd,
                gc0End,
                gc1End,
                gc2End,
                stReadMops,
                stLatency,
                mt8Result.Mops,
                mt8Result.Latency,
                mt16Result.Mops,
                mt16Result.Latency,
                zipf8Mops,
                zipf16Mops,
                scanMops,
                scanDuration,
                stWriteMops,
                mt8WriteMops,
                adjContentionWriteMops,
                mixed99Mops,
                mixed95Mops);

            results.Add(candResult);

            // Release references
            instance = null!;
            evaluator = null!;
            mutator = null!;
            accountable = null!;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        SaveTournamentResults(results, resultsDir);
        return results;
    }

    private static async Task<(double Mops, LatencyStats Latency)> RunMultiThreadReadAsync(
        IPredicateEvaluator<long> evaluator,
        long accountCount,
        int threads,
        int opsPerThread)
    {
        var latenciesSampled = new ConcurrentBag<double>();
        double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        long totalOps = 0;

        var sw = Stopwatch.StartNew();
        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int seed = 1000 + t;
            tasks[t] = Task.Run(() =>
            {
                var rand = new Random(seed);
                long localOps = 0;
                for (int i = 0; i < opsPerThread; i++)
                {
                    long accId = 1 + rand.Next(0, (int)accountCount);
                    int pred = i & 7;

                    if ((i & 255) == 0 && latenciesSampled.Count < 20_000)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        _ = evaluator.EvaluatePredicate(accId, pred);
                        long t1 = Stopwatch.GetTimestamp();
                        latenciesSampled.Add((t1 - t0) * nsPerTick);
                    }
                    else
                    {
                        _ = evaluator.EvaluatePredicate(accId, pred);
                    }
                    localOps++;
                }
                Interlocked.Add(ref totalOps, localOps);
            });
        }
        await Task.WhenAll(tasks);
        sw.Stop();

        double mops = (totalOps / sw.Elapsed.TotalSeconds) / 1_000_000.0;
        var latency = ComputeLatencyStats(latenciesSampled.ToList());
        return (mops, latency);
    }

    private static async Task<double> RunZipfianReadAsync(
        IPredicateEvaluator<long> evaluator,
        long accountCount,
        int threads,
        int opsPerThread)
    {
        int hotRange = Math.Max(1, (int)(accountCount / 100)); // 1% hot accounts (500,000)
        long totalOps = 0;

        var sw = Stopwatch.StartNew();
        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int seed = 7000 + t;
            tasks[t] = Task.Run(() =>
            {
                var rand = new Random(seed);
                long localOps = 0;
                for (int i = 0; i < opsPerThread; i++)
                {
                    // 80% hits top 1% hot accounts; 20% hits rest
                    long accId = rand.Next(100) < 80
                        ? 1 + rand.Next(0, hotRange)
                        : 1 + rand.Next(hotRange, (int)accountCount);

                    int pred = i & 7;
                    _ = evaluator.EvaluatePredicate(accId, pred);
                    localOps++;
                }
                Interlocked.Add(ref totalOps, localOps);
            });
        }
        await Task.WhenAll(tasks);
        sw.Stop();

        return (totalOps / sw.Elapsed.TotalSeconds) / 1_000_000.0;
    }

    private static async Task<double> RunMixedWorkloadAsync(
        IPredicateEvaluator<long> evaluator,
        ISourceMutationHandler mutator,
        long accountCount,
        int readerThreads,
        int writerThreads,
        double durationSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
        var token = cts.Token;
        long totalOps = 0;

        var tasks = new List<Task>();

        // Reader tasks
        for (int r = 0; r < readerThreads; r++)
        {
            int seed = 8000 + r;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(seed);
                long localReads = 0;
                while (!token.IsCancellationRequested)
                {
                    long accId = 1 + rand.Next(0, (int)accountCount);
                    int pred = (int)(localReads & 7);
                    _ = evaluator.EvaluatePredicate(accId, pred);
                    localReads++;
                }
                Interlocked.Add(ref totalOps, localReads);
            }));
        }

        // Writer tasks
        for (int w = 0; w < writerThreads; w++)
        {
            int seed = 9000 + w;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(seed);
                long localWrites = 0;
                while (!token.IsCancellationRequested)
                {
                    long accId = 1 + rand.Next(0, (int)accountCount);
                    mutator.HandleMutation(accId, (localWrites & 1) == 0 ? 50 : 150, (localWrites & 2) == 0, 400 + localWrites);
                    localWrites++;
                }
                Interlocked.Add(ref totalOps, localWrites);
            }));
        }

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(tasks);
        sw.Stop();

        return (totalOps / sw.Elapsed.TotalSeconds) / 1_000_000.0;
    }

    private static LatencyStats ComputeLatencyStats(List<double> samples)
    {
        if (samples.Count == 0) return new LatencyStats(0, 0, 0, 0);
        samples.Sort();
        int p50Idx = (int)(samples.Count * 0.50);
        int p95Idx = (int)(samples.Count * 0.95);
        int p99Idx = (int)(samples.Count * 0.99);

        return new LatencyStats(
            samples[p50Idx],
            samples[p95Idx],
            samples[p99Idx],
            samples[^1]);
    }

    private static void SaveTournamentResults(List<Candidate50MResult> results, string? resultsDir)
    {
        string baseDir = resultsDir ?? Path.Combine(Directory.GetCurrentDirectory(), "research", "NeuroCache.ResearchLab", "Results", "2026-10-29_50M_Validation");
        string rawDir = Path.Combine(baseDir, "Raw");
        string sumDir = Path.Combine(baseDir, "Summaries");

        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(sumDir);

        string jsonPath = Path.Combine(rawDir, "tournament-50m-raw.json");
        string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
        Console.WriteLine();
        Console.WriteLine($"[Results] Wrote raw tournament JSON to: {jsonPath}");

        string mdPath = Path.Combine(sumDir, "tournament-50m-summary.md");
        using var sw = new StreamWriter(mdPath);
        sw.WriteLine("# NeuroCache ResearchLab: 50,000,000 Record Tournament Results");
        sw.WriteLine();
        sw.WriteLine($"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ");
        sw.WriteLine($"**Target Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  ");
        sw.WriteLine($"**Host Platform:** AMD Ryzen 7 5825U (8 Physical Cores, 16 Logical Processors, 16 MB L3 Cache)  ");
        sw.WriteLine($"**Runtime:** .NET 10.0 x64, PostgreSQL 17.2  ");
        sw.WriteLine();
        sw.WriteLine("## 1. Candidate Footprint & Construction Metrics");
        sw.WriteLine();
        sw.WriteLine("| Candidate ID | Candidate Name | Family | Payload (MB) | Metadata (KB) | Total (MB) | Total (MiB) | Bits/Acc | Init Time (s) |");
        sw.WriteLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results.OrderBy(r => r.BitsPerAccount))
        {
            sw.WriteLine($"| `{r.CandidateId}` | {r.CandidateName} | {r.Family} | {r.PayloadBytes / 1_000_000.0:F2} | {r.MetadataBytes / 1024.0:F1} | {r.DecimalMb:F2} | {r.BinaryMib:F2} | {r.BitsPerAccount:F2} | {r.ConstructionDurationSeconds:F2}s |");
        }
        sw.WriteLine();
        sw.WriteLine("## 2. Read Throughput & Latency Tournament");
        sw.WriteLine();
        sw.WriteLine("| Candidate ID | ST Read (Mops/s) | MT 8T (Mops/s) | MT 16T (Mops/s) | ST p50 (ns) | ST p99 (ns) | MT8 p50 (ns) | MT8 p99 (ns) | Zipf 8T (Mops/s) | Seq Scan (Mops/s) |");
        sw.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results.OrderByDescending(r => r.MultiThread16ReadMops))
        {
            sw.WriteLine($"| `{r.CandidateId}` | {r.SingleThreadReadMops:F2} | {r.MultiThread8ReadMops:F2} | {r.MultiThread16ReadMops:F2} | {r.SingleThreadReadLatency.P50Ns:F1} | {r.SingleThreadReadLatency.P99Ns:F1} | {r.MultiThread8ReadLatency.P50Ns:F1} | {r.MultiThread8ReadLatency.P99Ns:F1} | {r.Zipfian8ReadMops:F2} | {r.SequentialScanMops:F2} |");
        }
        sw.WriteLine();
        sw.WriteLine("## 3. Write & Concurrency Tournament");
        sw.WriteLine();
        sw.WriteLine("| Candidate ID | ST Write (Mops/s) | MT8 Write (Mops/s) | Adj-Contention Write (Mops/s) | Mixed 99/1 (Mops/s) | Mixed 95/5 (Mops/s) | Working Set (MB) |");
        sw.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var r in results.OrderByDescending(r => r.Mixed99Read1WriteMops))
        {
            sw.WriteLine($"| `{r.CandidateId}` | {r.SingleThreadWriteMops:F2} | {r.MultiThread8WriteMops:F2} | {r.AdjacentContentionWriteMops:F2} | {r.Mixed99Read1WriteMops:F2} | {r.Mixed95Read5WriteMops:F2} | {r.FinalWorkingSetMb} |");
        }

        Console.WriteLine($"[Results] Wrote summary tournament report to: {mdPath}");
    }
}
