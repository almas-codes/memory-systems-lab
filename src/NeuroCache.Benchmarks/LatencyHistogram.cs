using System;
using System.Diagnostics;

namespace NeuroCache.Benchmarks;

public sealed class LatencyHistogram
{
    private readonly double[] _samples;
    private int _count;
    private readonly object _lock = new();

    public LatencyHistogram(int capacity = 2_000_000)
    {
        _samples = new double[capacity];
    }

    public void Record(double latencyMicroseconds)
    {
        int idx = Interlocked.Increment(ref _count) - 1;
        if (idx < _samples.Length)
        {
            _samples[idx] = latencyMicroseconds;
        }
    }

    public BenchmarkMetrics ComputeMetrics(TimeSpan duration, long totalOps, long allocatedBytesStart, int g0Start, int g1Start, int g2Start, long workingSetPeak)
    {
        int validCount = Math.Min(_count, _samples.Length);
        if (validCount == 0) return new BenchmarkMetrics { TotalOperations = totalOps };

        double[] sorted = new double[validCount];
        Array.Copy(_samples, sorted, validCount);
        Array.Sort(sorted);

        double p50 = sorted[(int)(validCount * 0.50)];
        double p95 = sorted[(int)(validCount * 0.95)];
        double p99 = sorted[(int)(validCount * 0.99)];
        double p999 = sorted[(int)(validCount * 0.999)];
        double min = sorted[0];
        double max = sorted[validCount - 1];

        double sum = 0;
        for (int i = 0; i < validCount; i++) sum += sorted[i];
        double avg = sum / validCount;

        long allocDiff = GC.GetTotalAllocatedBytes() - allocatedBytesStart;
        int g0Diff = GC.CollectionCount(0) - g0Start;
        int g1Diff = GC.CollectionCount(1) - g1Start;
        int g2Diff = GC.CollectionCount(2) - g2Start;

        double opsPerSec = duration.TotalSeconds > 0 ? totalOps / duration.TotalSeconds : 0;

        return new BenchmarkMetrics
        {
            TotalOperations = totalOps,
            Elapsed = duration,
            ThroughputOpsPerSec = opsPerSec,
            MinLatencyUs = min,
            AvgLatencyUs = avg,
            P50LatencyUs = p50,
            P95LatencyUs = p95,
            P99LatencyUs = p99,
            P999LatencyUs = p999,
            MaxLatencyUs = max,
            AllocatedBytesPerOp = totalOps > 0 ? (double)allocDiff / totalOps : 0,
            TotalAllocatedBytes = allocDiff,
            Gen0Collections = g0Diff,
            Gen1Collections = g1Diff,
            Gen2Collections = g2Diff,
            PeakWorkingSetBytes = workingSetPeak
        };
    }
}

public sealed class BenchmarkMetrics
{
    public long TotalOperations { get; set; }
    public TimeSpan Elapsed { get; set; }
    public double ThroughputOpsPerSec { get; set; }
    public double MinLatencyUs { get; set; }
    public double AvgLatencyUs { get; set; }
    public double P50LatencyUs { get; set; }
    public double P95LatencyUs { get; set; }
    public double P99LatencyUs { get; set; }
    public double P999LatencyUs { get; set; }
    public double MaxLatencyUs { get; set; }
    public double AllocatedBytesPerOp { get; set; }
    public long TotalAllocatedBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public long PeakWorkingSetBytes { get; set; }
}
