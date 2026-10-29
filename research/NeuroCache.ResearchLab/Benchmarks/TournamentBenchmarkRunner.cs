using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.HybridCompositions;
using NeuroCache.ResearchLab.Recovery;
using NeuroCache.ResearchLab.Representations;
using NeuroCache.ResearchLab.SemanticEncoding;

namespace NeuroCache.ResearchLab.Benchmarks;

public sealed class TournamentBenchmarkRunner
{
    public sealed record CandidateBenchmarkResult(
        string CandidateId,
        string Name,
        string Family,
        long AccountCount,
        long AllocatedBytes,
        double DecimalMb,
        double BinaryMib,
        double BitsPerAccount,
        double SingleThreadReadMops,
        double MultiThreadReadMops,
        double ZipfianReadMops,
        double MultiThreadWriteMops);

    public static async Task<List<CandidateBenchmarkResult>> RunTournamentAsync(
        int accountCount = 1_000_000,
        int iterationsPerThread = 1_000_000,
        int workerThreads = 8)
    {
        Console.WriteLine($"==========================================================================");
        Console.WriteLine($" NEUROCACHE RESEARCH LAB — CANDIDATE TOURNAMENT BENCHMARK");
        Console.WriteLine($" Accounts: {accountCount:N0} | Threads: {workerThreads} | Iterations: {iterationsPerThread:N0}");
        Console.WriteLine($"==========================================================================");

        var candidates = new (string Id, string Name, string Family, Func<object> Factory)[]
        {
            ("SEM-BITMAPS-001", "Independent Bitmaps (EAP Baseline)", "SemanticEncoding", () => new SEM01_IndependentBitmaps(accountCount)),
            ("SEM-BYTEVEC-002", "Byte Answer Vector", "SemanticEncoding", () => new SEM02_ByteAnswerVector(accountCount)),
            ("SEM-CLASS4-003", "4-Bit Semantic Classes", "SemanticEncoding", () => new SEM03_FourBitSemanticClasses(accountCount)),
            ("SEM-CLASS3-004", "3-Bit Semantic Classes", "SemanticEncoding", () => new SEM04_ThreeBitSemanticClasses(accountCount)),
            ("REP-CJB-001", "Correlated Outcome Joint Bitmap", "Representations", () => new REP01_CorrelatedOutcomeJointBitmap(accountCount)),
            ("REP-FSCG-002", "Factorized Shared Condition Graph", "Representations", () => new REP02_FactorizedSharedConditionGraph(accountCount)),
            ("REC-COW-001", "Copy-On-Write Answer Pages", "Recovery", () => new REC01_CopyOnWriteAnswerPages(accountCount)),
            ("REC-CCPR-002", "Checkpointed Prefix Replay", "Recovery", () => new REC02_CheckpointedContiguousPrefixReplay(accountCount)),
            ("REC-SEQLOCK-003", "Seqlock Page Publication", "Recovery", () => new REC03_SeqlockPagePublication(accountCount)),
            ("COMBO-FSCG-DARG-001", "Factorized Atoms + DARG + Epoch", "HybridCompositions", () => new COMBO01_FactorizedAtomsWithDargAndFreshness(accountCount)),
            ("COMBO-SEM-SPARSE-002", "Semantic Classes + Rare Exceptions", "HybridCompositions", () => new COMBO02_SemanticClassesWithRareExceptions(accountCount)),
            ("COMBO-OVERLAY-COW-003", "Bounded Overlay + CoW Generations", "HybridCompositions", () => new COMBO03_BoundedOverlayWithCowGenerations(accountCount)),
            ("COMBO-ATOMS-BATCH-004", "Shared Atoms + Batch SIMD Reuse", "HybridCompositions", () => new COMBO04_SharedAtomsWithBatchIntermediateReuse(accountCount)),
            ("COMBO-HOT-COLD-005", "Hot Materialization + Exact Cold Fallback", "HybridCompositions", () => new COMBO05_HotMaterializationWithExactColdFallback(accountCount)),
            ("COMBO-PORTFOLIO-006", "Complete Portfolio Router", "HybridCompositions", () => new COMBO06_CompletePortfolioRouter(accountCount)),
        };

        var results = new List<CandidateBenchmarkResult>();

        foreach (var c in candidates)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            object instance = c.Factory();
            var evaluator = (IPredicateEvaluator<long>)instance;
            var mutator = instance as ISourceMutationHandler;
            var accountable = instance as IMemoryAccountable;

            Console.Write($"[{c.Id}] Initializing {accountCount:N0} records... ");

            // Preload with 50M realistic distribution:
            if (mutator != null)
            {
                for (int i = 0; i < accountCount; i++)
                {
                    int patternType = i % 100;
                    int bal;
                    bool act;
                    if (patternType < 45) { act = true; bal = 50; } // Pattern 128
                    else if (patternType < 90) { act = true; bal = 150; } // Pattern 193
                    else if (patternType < 95) { act = false; bal = 50; } // Pattern 0
                    else { act = false; bal = 150; } // Pattern 1

                    mutator.HandleMutation(i, bal, act, 1);
                }
            }

            long bytes = accountable?.TotalAllocatedBytes ?? 0;
            double decimalMb = bytes / 1_000_000.0;
            double binaryMib = bytes / (1024.0 * 1024.0);
            double bitsPerAcc = (bytes * 8.0) / accountCount;

            Console.WriteLine($"Memory: {binaryMib:F2} MiB ({decimalMb:F2} MB, {bitsPerAcc:F2} bits/acc)");

            // Warmup
            for (int w = 0; w < 50_000; w++)
            {
                _ = evaluator.EvaluatePredicate(w % accountCount, w & 7);
            }

            // 1. Single-Thread Uniform Random Reads
            int stCount = 2_000_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < stCount; i++)
            {
                long acc = (i * 104729L) % accountCount;
                int pred = i & 7;
                _ = evaluator.EvaluatePredicate(acc, pred);
            }
            sw.Stop();
            double stReadMops = (stCount / sw.Elapsed.TotalSeconds) / 1_000_000.0;

            // 2. Multi-Thread Uniform Random Reads (8 Threads)
            long mtTotalOps = 0;
            sw.Restart();
            var tasks = new Task[workerThreads];
            for (int t = 0; t < workerThreads; t++)
            {
                int seed = 1000 + t;
                tasks[t] = Task.Run(() =>
                {
                    var rand = new Random(seed);
                    long localOps = 0;
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        long acc = rand.Next(0, accountCount);
                        int pred = i & 7;
                        _ = evaluator.EvaluatePredicate(acc, pred);
                        localOps++;
                    }
                    Interlocked.Add(ref mtTotalOps, localOps);
                });
            }
            await Task.WhenAll(tasks);
            sw.Stop();
            double mtReadMops = (mtTotalOps / sw.Elapsed.TotalSeconds) / 1_000_000.0;

            // 3. Zipfian Skewed Reads (80% traffic hitting 1% hot accounts)
            long zipfTotalOps = 0;
            int hotRange = Math.Max(1, accountCount / 100);
            sw.Restart();
            for (int t = 0; t < workerThreads; t++)
            {
                int seed = 5000 + t;
                tasks[t] = Task.Run(() =>
                {
                    var rand = new Random(seed);
                    long localOps = 0;
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        long acc = (rand.NextDouble() < 0.80) ? rand.Next(0, hotRange) : rand.Next(hotRange, accountCount);
                        int pred = i & 7;
                        _ = evaluator.EvaluatePredicate(acc, pred);
                        localOps++;
                    }
                    Interlocked.Add(ref zipfTotalOps, localOps);
                });
            }
            await Task.WhenAll(tasks);
            sw.Stop();
            double zipfReadMops = (zipfTotalOps / sw.Elapsed.TotalSeconds) / 1_000_000.0;

            // 4. Multi-Thread Contended Writes (if supported)
            double mtWriteMops = 0.0;
            if (mutator != null)
            {
                long mtWriteOps = 0;
                int writeIters = 200_000;
                sw.Restart();
                for (int t = 0; t < workerThreads; t++)
                {
                    int seed = 9000 + t;
                    tasks[t] = Task.Run(() =>
                    {
                        var rand = new Random(seed);
                        long local = 0;
                        for (int i = 0; i < writeIters; i++)
                        {
                            long acc = rand.Next(0, Math.Min(10_000, accountCount));
                            int bal = (i & 1) == 0 ? 50 : 200;
                            bool act = (i & 2) == 0;
                            mutator.HandleMutation(acc, bal, act, 100 + i);
                            local++;
                        }
                        Interlocked.Add(ref mtWriteOps, local);
                    });
                }
                await Task.WhenAll(tasks);
                sw.Stop();
                mtWriteMops = (mtWriteOps / sw.Elapsed.TotalSeconds) / 1_000_000.0;
            }

            Console.WriteLine($"  ST-Read: {stReadMops:F2} Mops/s | MT-Read: {mtReadMops:F2} Mops/s | Zipf: {zipfReadMops:F2} Mops/s | MT-Write: {mtWriteMops:F2} Mops/s");

            var result = new CandidateBenchmarkResult(
                c.Id,
                c.Name,
                c.Family,
                accountCount,
                bytes,
                decimalMb,
                binaryMib,
                bitsPerAcc,
                stReadMops,
                mtReadMops,
                zipfReadMops,
                mtWriteMops
            );

            results.Add(result);
        }

        return results;
    }
}
