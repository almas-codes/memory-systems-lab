using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Benchmarks;
using NeuroCache.Core;

namespace NeuroCache.EapResearch;

public sealed class EapBenchmarkResult
{
    public string CandidateName { get; set; } = "";
    public string WorkloadName { get; set; } = "";
    public long DatasetRowCount { get; set; }
    public double ThroughputOpsPerSec { get; set; }
    public double MinLatencyUs { get; set; }
    public double P50LatencyUs { get; set; }
    public double P95LatencyUs { get; set; }
    public double P99LatencyUs { get; set; }
    public double MaxLatencyUs { get; set; }
    public double AllocBytesPerOp { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public long ResidentBytes { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EapBenchmarkRunner
{
    public static async Task<EapBenchmarkResult> RunBenchmarkAsync(
        IEapBenchmarkCandidate candidate,
        string workloadName,
        long maxId,
        int totalRequests = 10_000,
        double readPercent = 100.0,
        bool zipfian = false,
        int seed = 42,
        CancellationToken ct = default)
    {
        Random rng = new(seed);
        long hotSetSize = Math.Max(100, maxId / 100); // 1% hot set

        var histogram = new LatencyHistogram(totalRequests);

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long startAlloc = GC.GetTotalAllocatedBytes();
        var proc = Process.GetCurrentProcess();
        long peakWs = proc.WorkingSet64;

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < totalRequests; i++)
        {
            ct.ThrowIfCancellationRequested();

            long id;
            if (zipfian)
            {
                id = rng.NextDouble() < 0.85
                    ? rng.NextInt64(1, hotSetSize + 1)
                    : rng.NextInt64(hotSetSize + 1, maxId + 1);
            }
            else
            {
                id = rng.NextInt64(1, maxId + 1);
            }

            bool isRead = rng.NextDouble() * 100.0 < readPercent;

            long t0 = Stopwatch.GetTimestamp();
            if (isRead)
            {
                await candidate.QueryEligibleAsync(id, 100, ct);
            }
            else
            {
                int newBalance = rng.Next(0, 200);
                bool newActive = rng.Next(0, 10) != 0;
                await candidate.UpdateAccountAsync(id, newBalance, newActive, ct);
            }
            long t1 = Stopwatch.GetTimestamp();

            double us = (double)(t1 - t0) * 1_000_000 / Stopwatch.Frequency;
            histogram.Record(us);
        }

        sw.Stop();
        proc.Refresh();
        peakWs = Math.Max(peakWs, proc.WorkingSet64);

        var metrics = histogram.ComputeMetrics(sw.Elapsed, totalRequests, startAlloc, 0, 0, 0, peakWs);

        return new EapBenchmarkResult
        {
            CandidateName = candidate.Name,
            WorkloadName = workloadName,
            DatasetRowCount = maxId,
            ThroughputOpsPerSec = metrics.ThroughputOpsPerSec,
            MinLatencyUs = metrics.MinLatencyUs,
            P50LatencyUs = metrics.P50LatencyUs,
            P95LatencyUs = metrics.P95LatencyUs,
            P99LatencyUs = metrics.P99LatencyUs,
            MaxLatencyUs = metrics.MaxLatencyUs,
            AllocBytesPerOp = metrics.AllocatedBytesPerOp,
            PeakWorkingSetBytes = peakWs,
            ResidentBytes = candidate.EstimatedResidentBytes
        };
    }

    public static void SaveResults(List<EapBenchmarkResult> results, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string jsonPath = Path.Combine(outputDirectory, "eap_benchmark_results.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        string jsonlPath = Path.Combine(outputDirectory, "eap_benchmark_results.jsonl");
        using var writer = new StreamWriter(jsonlPath, append: false);
        foreach (var r in results)
        {
            writer.WriteLine(JsonSerializer.Serialize(r));
        }
    }
}
