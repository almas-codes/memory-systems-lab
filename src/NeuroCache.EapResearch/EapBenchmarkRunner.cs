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
    public int Repetition { get; set; }
    public double ThroughputOpsPerSec { get; set; }

    // Combined stream latency
    public double MinLatencyUs { get; set; }
    public double P50LatencyUs { get; set; }
    public double P95LatencyUs { get; set; }
    public double P99LatencyUs { get; set; }
    public double P999LatencyUs { get; set; }
    public double MaxLatencyUs { get; set; }

    // Separate Read Stream Metrics
    public long CompletedReads { get; set; }
    public double ReadMeanLatencyUs { get; set; }
    public double ReadP50LatencyUs { get; set; }
    public double ReadP95LatencyUs { get; set; }
    public double ReadP99LatencyUs { get; set; }

    // Separate Write Stream Metrics
    public long CompletedWrites { get; set; }
    public long CompletedOutboxInserts { get; set; }
    public long CompletedCommits { get; set; }
    public long FailedOperations { get; set; }
    public double WriteMeanLatencyUs { get; set; }
    public double WriteP50LatencyUs { get; set; }
    public double WriteP95LatencyUs { get; set; }
    public double WriteP99LatencyUs { get; set; }

    public double ElapsedWallClockMs { get; set; }
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
        bool boundaryChurn = false,
        int repetition = 1,
        int seed = 42,
        CancellationToken ct = default)
    {
        Random rng = new(seed + repetition * 17);
        long hotSetSize = Math.Max(100, maxId / 100); // 1% hot set

        var combinedHistogram = new LatencyHistogram(totalRequests);
        var readHistogram = new LatencyHistogram(totalRequests);
        var writeHistogram = new LatencyHistogram(totalRequests);

        long completedReads = 0;
        long completedWrites = 0;
        long failedOps = 0;

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
                try
                {
                    await candidate.QueryEligibleAsync(id, 100, ct);
                    long t1 = Stopwatch.GetTimestamp();
                    double us = (double)(t1 - t0) * 1_000_000 / Stopwatch.Frequency;
                    combinedHistogram.Record(us);
                    readHistogram.Record(us);
                    completedReads++;
                }
                catch
                {
                    failedOps++;
                }
            }
            else
            {
                int newBalance;
                bool newActive;

                if (boundaryChurn)
                {
                    // Boundary churn oscillates across threshold 100 (e.g., 95 <-> 105)
                    newBalance = (i % 2 == 0) ? 95 : 105;
                    newActive = true;
                }
                else
                {
                    newBalance = rng.Next(0, 200);
                    newActive = rng.Next(0, 10) != 0;
                }

                try
                {
                    bool ok = await candidate.UpdateAccountAsync(id, newBalance, newActive, ct);
                    if (ok) completedWrites++;
                    else failedOps++;

                    long t1 = Stopwatch.GetTimestamp();
                    double us = (double)(t1 - t0) * 1_000_000 / Stopwatch.Frequency;
                    combinedHistogram.Record(us);
                    writeHistogram.Record(us);
                }
                catch
                {
                    failedOps++;
                }
            }
        }

        sw.Stop();
        proc.Refresh();
        peakWs = Math.Max(peakWs, proc.WorkingSet64);

        int totalCompleted = (int)(completedReads + completedWrites);
        var combinedMetrics = combinedHistogram.ComputeMetrics(sw.Elapsed, totalCompleted, startAlloc, 0, 0, 0, peakWs);

        var readMetrics = completedReads > 0
            ? readHistogram.ComputeMetrics(sw.Elapsed, (int)completedReads, 0, 0, 0, 0, peakWs)
            : default;

        var writeMetrics = completedWrites > 0
            ? writeHistogram.ComputeMetrics(sw.Elapsed, (int)completedWrites, 0, 0, 0, 0, peakWs)
            : default;

        double opsPerSec = totalCompleted > 0 && sw.Elapsed.TotalSeconds > 0
            ? totalCompleted / sw.Elapsed.TotalSeconds
            : 0;

        return new EapBenchmarkResult
        {
            CandidateName = candidate.Name,
            WorkloadName = workloadName,
            DatasetRowCount = maxId,
            Repetition = repetition,
            ThroughputOpsPerSec = opsPerSec,
            MinLatencyUs = combinedMetrics.MinLatencyUs,
            P50LatencyUs = combinedMetrics.P50LatencyUs,
            P95LatencyUs = combinedMetrics.P95LatencyUs,
            P99LatencyUs = combinedMetrics.P99LatencyUs,
            P999LatencyUs = combinedMetrics.P999LatencyUs,
            MaxLatencyUs = combinedMetrics.MaxLatencyUs,
            CompletedReads = completedReads,
            ReadMeanLatencyUs = readMetrics?.AvgLatencyUs ?? 0,
            ReadP50LatencyUs = readMetrics?.P50LatencyUs ?? 0,
            ReadP95LatencyUs = readMetrics?.P95LatencyUs ?? 0,
            ReadP99LatencyUs = readMetrics?.P99LatencyUs ?? 0,
            CompletedWrites = completedWrites,
            CompletedOutboxInserts = completedWrites, // Each successful write inserts into outbox
            CompletedCommits = completedWrites,       // Each successful write commits
            FailedOperations = failedOps,
            WriteMeanLatencyUs = writeMetrics?.AvgLatencyUs ?? 0,
            WriteP50LatencyUs = writeMetrics?.P50LatencyUs ?? 0,
            WriteP95LatencyUs = writeMetrics?.P95LatencyUs ?? 0,
            WriteP99LatencyUs = writeMetrics?.P99LatencyUs ?? 0,
            ElapsedWallClockMs = sw.Elapsed.TotalMilliseconds,
            AllocBytesPerOp = combinedMetrics.AllocatedBytesPerOp,
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
