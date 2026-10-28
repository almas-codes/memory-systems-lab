using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Strategies;

namespace NeuroCache.Benchmarks;

public sealed class BenchmarkSuites
{
    public static List<IStorageStrategy> CreateAllStrategies()
    {
        return new List<IStorageStrategy>
        {
            new DirectBaselineStrategy(),
            new DotNetDictionaryStrategy(),
            new DotNetMemoryCacheStrategy(),
            new CertusEngramStrategy(),
            new SufficiencyFabricStrategy(),
            new NeuroCacheOmegaStrategy(),
            new ProjectionCapsuleStrategy(),
            new MemoryMappedStoreStrategy()
        };
    }

    public static async Task<List<BenchmarkRunResult>> RunSmokeSuiteAsync(
        string dataDirectory,
        RecordManifest manifest,
        string resultsDirectory,
        CancellationToken ct = default)
    {
        var results = new List<BenchmarkRunResult>();
        int requestCount = 5_000;
        var requests = WorkloadGenerator.GenerateWorkload(requestCount, manifest.RecordCount, WorkloadDistribution.ZipfSkewed, readPercent: 95.0, seed: 42);

        var strategies = CreateAllStrategies();
        var options = new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024 };

        foreach (var strategy in strategies)
        {
            await using (strategy)
            {
                await strategy.InitializeAsync(dataDirectory, manifest, options, ct).ConfigureAwait(false);
                var res = await BenchmarkHarness.ExecuteBenchmarkAsync(strategy, "Smoke-Zipf95", requests, warmupOperations: 500, concurrency: 1, ramBudgetMb: 256, ct).ConfigureAwait(false);
                results.Add(res);
            }
        }

        SaveResults(results, Path.Combine(resultsDirectory, "smoke_suite.json"));
        return results;
    }

    public static async Task<List<BenchmarkRunResult>> RunMicroSuiteAsync(
        string dataDirectory,
        RecordManifest manifest,
        string resultsDirectory,
        CancellationToken ct = default)
    {
        var results = new List<BenchmarkRunResult>();
        int requestCount = 20_000;

        // Micro 1: Recompute vs SSD Read
        var pureComputeRequests = WorkloadGenerator.GenerateWorkload(requestCount, manifest.RecordCount, WorkloadDistribution.ZipfSkewed, readPercent: 100.0, seed: 101);
        foreach (var req in pureComputeRequests) req.OpType = OperationType.ComputeDerived;

        // Test Direct Baseline (SSD Read) vs Sufficiency Fabric (Seed Recompute) vs NeuroCache Omega (Answer Region / Recompute)
        var testedStrategies = new List<IStorageStrategy>
        {
            new DirectBaselineStrategy(),
            new MemoryMappedStoreStrategy(),
            new SufficiencyFabricStrategy(),
            new NeuroCacheOmegaStrategy()
        };

        var options = new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024 };
        foreach (var strat in testedStrategies)
        {
            await using (strat)
            {
                await strat.InitializeAsync(dataDirectory, manifest, options, ct).ConfigureAwait(false);
                var res = await BenchmarkHarness.ExecuteBenchmarkAsync(strat, "Micro-PureRecomputeVsSSD", pureComputeRequests, warmupOperations: 1000, concurrency: 1, ramBudgetMb: 256, ct).ConfigureAwait(false);
                results.Add(res);
            }
        }

        // Micro 2: Single-Read Capsule vs Direct Baseline Extent Read
        var projRequests = WorkloadGenerator.GenerateWorkload(requestCount, manifest.RecordCount, WorkloadDistribution.Uniform, readPercent: 100.0, seed: 102);
        foreach (var req in projRequests) req.OpType = OperationType.FieldProjection;

        var projStrategies = new List<IStorageStrategy>
        {
            new DirectBaselineStrategy(),
            new ProjectionCapsuleStrategy(),
            new NeuroCacheOmegaStrategy()
        };

        foreach (var strat in projStrategies)
        {
            await using (strat)
            {
                await strat.InitializeAsync(dataDirectory, manifest, options, ct).ConfigureAwait(false);
                var res = await BenchmarkHarness.ExecuteBenchmarkAsync(strat, "Micro-SingleReadCapsule", projRequests, warmupOperations: 1000, concurrency: 1, ramBudgetMb: 256, ct).ConfigureAwait(false);
                results.Add(res);
            }
        }

        SaveResults(results, Path.Combine(resultsDirectory, "micro_suite.json"));
        return results;
    }

    public static async Task<List<BenchmarkRunResult>> RunFullMatrixSuiteAsync(
        string dataDirectory,
        RecordManifest manifest,
        string resultsDirectory,
        int opsPerRun = 25_000,
        int concurrency = 4,
        CancellationToken ct = default)
    {
        var results = new List<BenchmarkRunResult>();
        var options = new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024, MaxConcurrency = concurrency };

        var workloads = new (string Name, WorkloadDistribution Dist, double ReadPct)[]
        {
            ("Uniform-ReadOnly", WorkloadDistribution.Uniform, 100.0),
            ("Zipf-99Read-1Write", WorkloadDistribution.ZipfSkewed, 99.0),
            ("Zipf-95Read-5Write", WorkloadDistribution.ZipfSkewed, 95.0),
            ("Zipf-80Read-20Write", WorkloadDistribution.ZipfSkewed, 80.0),
            ("WorkingSet-FitInRAM", WorkloadDistribution.WorkingSetFit, 95.0),
            ("WorkingSet-2xRAM", WorkloadDistribution.WorkingSet2xRAM, 95.0),
            ("WorkingSet-10xRAM", WorkloadDistribution.WorkingSet10xRAM, 95.0),
            ("BurstyAccess", WorkloadDistribution.Bursty, 95.0)
        };

        foreach (var (wName, dist, readPct) in workloads)
        {
            var requests = WorkloadGenerator.GenerateWorkload(opsPerRun, manifest.RecordCount, dist, readPct, seed: 777);

            var strategies = CreateAllStrategies();
            foreach (var strategy in strategies)
            {
                await using (strategy)
                {
                    await strategy.InitializeAsync(dataDirectory, manifest, options, ct).ConfigureAwait(false);
                    var res = await BenchmarkHarness.ExecuteBenchmarkAsync(strategy, wName, requests, warmupOperations: 2000, concurrency: concurrency, ramBudgetMb: 256, ct).ConfigureAwait(false);
                    results.Add(res);
                }
            }
        }

        SaveResults(results, Path.Combine(resultsDirectory, "full_matrix_suite.json"));
        return results;
    }

    public static async Task<List<BenchmarkRunResult>> RunBudgetSuiteAsync(
        string dataDirectory,
        RecordManifest manifest,
        string resultsDirectory,
        int opsPerRun = 20_000,
        CancellationToken ct = default)
    {
        var results = new List<BenchmarkRunResult>();
        long[] budgetsMb = { 128, 256, 512, 1024 };

        var requests = WorkloadGenerator.GenerateWorkload(opsPerRun, manifest.RecordCount, WorkloadDistribution.WorkingSet2xRAM, readPercent: 95.0, seed: 888);

        foreach (long budgetMb in budgetsMb)
        {
            var options = new StrategyOptions { RamBudgetBytes = budgetMb * 1024 * 1024 };
            var strategies = CreateAllStrategies();

            foreach (var strategy in strategies)
            {
                await using (strategy)
                {
                    await strategy.InitializeAsync(dataDirectory, manifest, options, ct).ConfigureAwait(false);
                    var res = await BenchmarkHarness.ExecuteBenchmarkAsync(strategy, $"Budget-{budgetMb}MB", requests, warmupOperations: 1500, concurrency: 4, ramBudgetMb: budgetMb, ct).ConfigureAwait(false);
                    results.Add(res);
                }
            }
        }

        SaveResults(results, Path.Combine(resultsDirectory, "budget_suite.json"));
        return results;
    }

    public static async Task<List<BenchmarkRunResult>> RunAblationSuiteAsync(
        string dataDirectory,
        RecordManifest manifest,
        string resultsDirectory,
        int opsPerRun = 25_000,
        CancellationToken ct = default)
    {
        var results = new List<BenchmarkRunResult>();
        var requests = WorkloadGenerator.GenerateWorkload(opsPerRun, manifest.RecordCount, WorkloadDistribution.ZipfSkewed, readPercent: 90.0, seed: 999);

        var configurations = new (string Name, StrategyOptions Opts)[]
        {
            ("Omega-Full-AllEnabled", new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024, EnableSharedWitnesses = true, EnableRecomputation = true, EnableCapsules = true }),
            ("Omega-NoWitnesses", new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024, EnableSharedWitnesses = false, EnableRecomputation = true, EnableCapsules = true }),
            ("Omega-NoRecomputation", new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024, EnableSharedWitnesses = true, EnableRecomputation = false, EnableCapsules = true }),
            ("Omega-NoCapsules", new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024, EnableSharedWitnesses = true, EnableRecomputation = true, EnableCapsules = false }),
            ("Omega-Minimal", new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024, EnableSharedWitnesses = false, EnableRecomputation = false, EnableCapsules = false })
        };

        foreach (var (name, opts) in configurations)
        {
            var strategy = new NeuroCacheOmegaStrategy();
            await using (strategy)
            {
                await strategy.InitializeAsync(dataDirectory, manifest, opts, ct).ConfigureAwait(false);
                var res = await BenchmarkHarness.ExecuteBenchmarkAsync(strategy, name, requests, warmupOperations: 2000, concurrency: 4, ramBudgetMb: 256, ct).ConfigureAwait(false);
                results.Add(res);
            }
        }

        SaveResults(results, Path.Combine(resultsDirectory, "ablation_suite.json"));
        return results;
    }

    public static async Task<List<BenchmarkRunResult>> RunAdversarialSuiteAsync(
        string dataDirectory,
        RecordManifest manifest,
        string resultsDirectory,
        int opsPerRun = 20_000,
        CancellationToken ct = default)
    {
        var results = new List<BenchmarkRunResult>();
        var options = new StrategyOptions { RamBudgetBytes = 256 * 1024 * 1024 };

        // 1. Adversarial stride
        var advRequests = WorkloadGenerator.GenerateWorkload(opsPerRun, manifest.RecordCount, WorkloadDistribution.Adversarial, readPercent: 95.0, seed: 1234);

        // 2. High Churn writes (50% writes)
        var churnRequests = WorkloadGenerator.GenerateWorkload(opsPerRun, manifest.RecordCount, WorkloadDistribution.ZipfSkewed, readPercent: 50.0, seed: 5678);

        var strategies = CreateAllStrategies();
        foreach (var strategy in strategies)
        {
            await using (strategy)
            {
                await strategy.InitializeAsync(dataDirectory, manifest, options, ct).ConfigureAwait(false);
                var resAdv = await BenchmarkHarness.ExecuteBenchmarkAsync(strategy, "Adversarial-CacheDefeatStride", advRequests, warmupOperations: 1000, concurrency: 4, ramBudgetMb: 256, ct).ConfigureAwait(false);
                results.Add(resAdv);
            }
        }

        foreach (var strategy in strategies)
        {
            await using (strategy)
            {
                await strategy.InitializeAsync(dataDirectory, manifest, options, ct).ConfigureAwait(false);
                var resChurn = await BenchmarkHarness.ExecuteBenchmarkAsync(strategy, "Adversarial-HighChurn50Percent", churnRequests, warmupOperations: 1000, concurrency: 4, ramBudgetMb: 256, ct).ConfigureAwait(false);
                results.Add(resChurn);
            }
        }

        SaveResults(results, Path.Combine(resultsDirectory, "adversarial_suite.json"));
        return results;
    }

    private static void SaveResults(List<BenchmarkRunResult> results, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);

        // Also write JSONL for machine streaming
        string jsonlPath = Path.ChangeExtension(filePath, ".jsonl");
        using var writer = new StreamWriter(jsonlPath, append: false);
        foreach (var item in results)
        {
            writer.WriteLine(JsonSerializer.Serialize(item));
        }
    }
}
