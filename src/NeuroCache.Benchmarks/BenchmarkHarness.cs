using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;

namespace NeuroCache.Benchmarks;

public sealed class BenchmarkRunResult
{
    public string StrategyName { get; set; } = "";
    public string WorkloadName { get; set; } = "";
    public int Concurrency { get; set; }
    public long RamBudgetMb { get; set; }
    public BenchmarkMetrics Metrics { get; set; } = new();
    public StrategyStats Stats { get; set; } = new();
    public HostEnvironmentInfo HostInfo { get; set; } = new();
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BenchmarkHarness
{
    public static async Task<BenchmarkRunResult> ExecuteBenchmarkAsync(
        IStorageStrategy strategy,
        string workloadName,
        List<WorkloadRequest> requests,
        int warmupOperations = 2_000,
        int concurrency = 1,
        long ramBudgetMb = 256,
        CancellationToken ct = default)
    {
        // 1. Warm-up phase
        int warmupCount = Math.Min(warmupOperations, requests.Count / 5);
        for (int i = 0; i < warmupCount; i++)
        {
            var req = requests[i];
            await ExecuteSingleRequest(strategy, req, ct).ConfigureAwait(false);
        }

        // Reset stats after warmup
        strategy.ResetStats();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        long allocStart = GC.GetTotalAllocatedBytes();
        int g0 = GC.CollectionCount(0);
        int g1 = GC.CollectionCount(1);
        int g2 = GC.CollectionCount(2);

        var proc = Process.GetCurrentProcess();
        long peakWs = proc.WorkingSet64;

        var histogram = new LatencyHistogram(requests.Count);
        var stopwatch = Stopwatch.StartNew();

        if (concurrency <= 1)
        {
            for (int i = 0; i < requests.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var req = requests[i];

                long tStart = Stopwatch.GetTimestamp();
                await ExecuteSingleRequest(strategy, req, ct).ConfigureAwait(false);
                long tEnd = Stopwatch.GetTimestamp();

                double us = (double)(tEnd - tStart) * 1_000_000 / Stopwatch.Frequency;
                histogram.Record(us);
            }
        }
        else
        {
            int batchSize = (requests.Count + concurrency - 1) / concurrency;
            var tasks = new List<Task>();

            for (int t = 0; t < concurrency; t++)
            {
                int start = t * batchSize;
                int end = Math.Min(start + batchSize, requests.Count);
                if (start >= end) break;

                tasks.Add(Task.Run(async () =>
                {
                    for (int i = start; i < end; i++)
                    {
                        var req = requests[i];
                        long tStart = Stopwatch.GetTimestamp();
                        await ExecuteSingleRequest(strategy, req, ct).ConfigureAwait(false);
                        long tEnd = Stopwatch.GetTimestamp();

                        double us = (double)(tEnd - tStart) * 1_000_000 / Stopwatch.Frequency;
                        histogram.Record(us);
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        stopwatch.Stop();
        proc.Refresh();
        peakWs = Math.Max(peakWs, proc.WorkingSet64);

        var metrics = histogram.ComputeMetrics(stopwatch.Elapsed, requests.Count, allocStart, g0, g1, g2, peakWs);

        return new BenchmarkRunResult
        {
            StrategyName = strategy.Name,
            WorkloadName = workloadName,
            Concurrency = concurrency,
            RamBudgetMb = ramBudgetMb,
            Metrics = metrics,
            Stats = strategy.GetStats(),
            HostInfo = HostEnvironmentInfo.Capture()
        };
    }

    private static async ValueTask<StrategyResult> ExecuteSingleRequest(IStorageStrategy strategy, WorkloadRequest req, CancellationToken ct)
    {
        return req.OpType switch
        {
            OperationType.ExactGet => await strategy.GetAsync(req.Key, ct).ConfigureAwait(false),
            OperationType.FieldProjection => await strategy.GetProjectionAsync(req.Key, (int)(req.Param % 2), ct).ConfigureAwait(false),
            OperationType.ComputeDerived => await strategy.ComputeDerivedAsync(req.Key, req.Param, ct).ConfigureAwait(false),
            OperationType.EvaluatePredicate => await strategy.EvaluatePredicateAsync(req.Key, req.Param, ct).ConfigureAwait(false),
            OperationType.Upsert when req.MutationRecord != null => (await strategy.UpsertAsync(req.MutationRecord, ct).ConfigureAwait(false)) ? StrategyResult.FromInline(1) : StrategyResult.Miss,
            OperationType.Invalidate => await InvalidateRecord(strategy, req.Key, ct).ConfigureAwait(false),
            _ => await strategy.GetAsync(req.Key, ct).ConfigureAwait(false)
        };
    }

    private static async ValueTask<StrategyResult> InvalidateRecord(IStorageStrategy strategy, ulong key, CancellationToken ct)
    {
        await strategy.InvalidateAsync(key, ct).ConfigureAwait(false);
        return StrategyResult.FromInline(1);
    }
}
