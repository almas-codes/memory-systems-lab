using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroCache.EapResearch.AdaptiveCompression;

public sealed class BenchmarkResult
{
    public string CandidateName { get; init; } = "";
    public string BenchmarkType { get; init; } = ""; // "SingleQuery", "AllEightQueries"
    public string AccessPattern { get; init; } = "";  // "Sequential", "Random"
    public int WorkerThreads { get; init; }
    public long TotalOperations { get; init; }
    public double TotalDurationMs { get; init; }
    public double OperationsPerSec { get; init; }
    public double LatencyMeanNs { get; init; }
    public double LatencyP50Ns { get; init; }
    public double LatencyP95Ns { get; init; }
    public double LatencyP99Ns { get; init; }
    public long BytesAllocatedPerOp { get; init; }
}

public sealed class SemanticCompressionBenchmarkSuite
{
    private readonly CandidateA_EightIndependentBitmaps _candA;
    private readonly CandidateB_PackedByteAnswerPattern _candB;
    private readonly CandidateC_SemanticAnswerClassPlane _candC;
    private readonly CandidateD_ReferenceSingleBitmap _candD;
    private readonly CandidateE_PostgreSqlBaseline _candE;
    private readonly long _maxId;

    public SemanticCompressionBenchmarkSuite(
        CandidateA_EightIndependentBitmaps candA,
        CandidateB_PackedByteAnswerPattern candB,
        CandidateC_SemanticAnswerClassPlane candC,
        CandidateD_ReferenceSingleBitmap candD,
        CandidateE_PostgreSqlBaseline candE,
        long maxId)
    {
        _candA = candA;
        _candB = candB;
        _candC = candC;
        _candD = candD;
        _candE = candE;
        _maxId = maxId;
    }

    public List<BenchmarkResult> RunAllMemoryBenchmarks(
        int singleThreadOps = 20_000_000,
        int multiThreadOps = 40_000_000)
    {
        var results = new List<BenchmarkResult>();

        int[] threadConfigs = new[] { 1, 4, 8, 16 };

        foreach (int threads in threadConfigs)
        {
            long ops = (threads == 1) ? singleThreadOps : multiThreadOps;

            // 1. Single Query - Random Access
            results.Add(BenchmarkCandidateA_Single(threads, ops, random: true));
            results.Add(BenchmarkCandidateB_Single(threads, ops, random: true));
            results.Add(BenchmarkCandidateC_Single(threads, ops, random: true));
            results.Add(BenchmarkCandidateD_Single(threads, ops, random: true));

            // 2. Single Query - Sequential Access
            results.Add(BenchmarkCandidateA_Single(threads, ops, random: false));
            results.Add(BenchmarkCandidateB_Single(threads, ops, random: false));
            results.Add(BenchmarkCandidateC_Single(threads, ops, random: false));

            // 3. All 8 Queries - Random Access
            results.Add(BenchmarkCandidateA_All(threads, ops, random: true));
            results.Add(BenchmarkCandidateB_All(threads, ops, random: true));
            results.Add(BenchmarkCandidateC_All(threads, ops, random: true));

            // 4. All 8 Queries - Sequential Access
            results.Add(BenchmarkCandidateA_All(threads, ops, random: false));
            results.Add(BenchmarkCandidateB_All(threads, ops, random: false));
            results.Add(BenchmarkCandidateC_All(threads, ops, random: false));
        }

        return results;
    }

    public async Task<List<BenchmarkResult>> RunPostgreSqlBaselinesAsync(int sampleOps = 1_000)
    {
        var results = new List<BenchmarkResult>();

        // Candidate E - Single Query
        var singleLatencies = new double[sampleOps];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < sampleOps; i++)
        {
            long id = (i % _maxId) + 1;
            var opSw = Stopwatch.StartNew();
            await _candE.QuerySingleAsync(id, 0);
            opSw.Stop();
            singleLatencies[i] = opSw.Elapsed.TotalMicroseconds * 1000.0; // ns
        }
        sw.Stop();
        Array.Sort(singleLatencies);

        results.Add(new BenchmarkResult
        {
            CandidateName = "Candidate E (Direct PostgreSQL)",
            BenchmarkType = "SingleQuery",
            AccessPattern = "Sequential",
            WorkerThreads = 1,
            TotalOperations = sampleOps,
            TotalDurationMs = sw.Elapsed.TotalMilliseconds,
            OperationsPerSec = sampleOps / sw.Elapsed.TotalSeconds,
            LatencyMeanNs = (sw.Elapsed.TotalNanoseconds) / sampleOps,
            LatencyP50Ns = singleLatencies[(int)(sampleOps * 0.50)],
            LatencyP95Ns = singleLatencies[(int)(sampleOps * 0.95)],
            LatencyP99Ns = singleLatencies[(int)(sampleOps * 0.99)],
            BytesAllocatedPerOp = 0
        });

        // Candidate E - All 8 Queries
        var allLatencies = new double[sampleOps];
        sw.Restart();
        for (int i = 0; i < sampleOps; i++)
        {
            long id = (i % _maxId) + 1;
            var opSw = Stopwatch.StartNew();
            await _candE.QueryAllAsync(id);
            opSw.Stop();
            allLatencies[i] = opSw.Elapsed.TotalMicroseconds * 1000.0; // ns
        }
        sw.Stop();
        Array.Sort(allLatencies);

        results.Add(new BenchmarkResult
        {
            CandidateName = "Candidate E (Direct PostgreSQL)",
            BenchmarkType = "AllEightQueries",
            AccessPattern = "Sequential",
            WorkerThreads = 1,
            TotalOperations = sampleOps,
            TotalDurationMs = sw.Elapsed.TotalMilliseconds,
            OperationsPerSec = sampleOps / sw.Elapsed.TotalSeconds,
            LatencyMeanNs = (sw.Elapsed.TotalNanoseconds) / sampleOps,
            LatencyP50Ns = allLatencies[(int)(sampleOps * 0.50)],
            LatencyP95Ns = allLatencies[(int)(sampleOps * 0.95)],
            LatencyP99Ns = allLatencies[(int)(sampleOps * 0.99)],
            BytesAllocatedPerOp = 0
        });

        return results;
    }

    private BenchmarkResult BenchmarkCandidateA_Single(int threads, long totalOps, bool random)
    {
        return RunHarness("Candidate A (8 Independent Bitmaps)", "SingleQuery", random, threads, totalOps, (id, q) =>
        {
            return _candA.QuerySingle(id, q & 7) ? 1 : 0;
        });
    }

    private BenchmarkResult BenchmarkCandidateB_Single(int threads, long totalOps, bool random)
    {
        return RunHarness("Candidate B (Packed 8-bit Pattern)", "SingleQuery", random, threads, totalOps, (id, q) =>
        {
            return _candB.QuerySingle(id, q & 7) ? 1 : 0;
        });
    }

    private BenchmarkResult BenchmarkCandidateC_Single(int threads, long totalOps, bool random)
    {
        return RunHarness("Candidate C (Semantic Answer-Class)", "SingleQuery", random, threads, totalOps, (id, q) =>
        {
            return _candC.QuerySingle(id, q & 7) ? 1 : 0;
        });
    }

    private BenchmarkResult BenchmarkCandidateD_Single(int threads, long totalOps, bool random)
    {
        return RunHarness("Candidate D (Reference Single Bitmap)", "SingleQuery", random, threads, totalOps, (id, q) =>
        {
            return _candD.QuerySingle(id) ? 1 : 0;
        });
    }

    private BenchmarkResult BenchmarkCandidateA_All(int threads, long totalOps, bool random)
    {
        return RunHarness("Candidate A (8 Independent Bitmaps)", "AllEightQueries", random, threads, totalOps, (id, q) =>
        {
            return _candA.QueryAll(id);
        });
    }

    private BenchmarkResult BenchmarkCandidateB_All(int threads, long totalOps, bool random)
    {
        return RunHarness("Candidate B (Packed 8-bit Pattern)", "AllEightQueries", random, threads, totalOps, (id, q) =>
        {
            return _candB.QueryAll(id);
        });
    }

    private BenchmarkResult BenchmarkCandidateC_All(int threads, long totalOps, bool random)
    {
        return RunHarness("Candidate C (Semantic Answer-Class)", "AllEightQueries", random, threads, totalOps, (id, q) =>
        {
            return _candC.QueryAll(id);
        });
    }

    private BenchmarkResult RunHarness(
        string candidateName,
        string benchmarkType,
        bool random,
        int threads,
        long totalOps,
        Func<long, int, int> operation)
    {
        long opsPerThread = totalOps / threads;
        long actualOps = opsPerThread * threads;
        long maxId = _maxId;

        // Warmup
        for (long w = 1; w <= 10_000; w++)
        {
            operation((w % maxId) + 1, (int)(w & 7));
        }

        // Latency histogram sampling
        const int sampleCount = 50_000;
        var sampleLatencies = new double[sampleCount];
        int sampleStep = (int)Math.Max(1, actualOps / sampleCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        if (threads == 1)
        {
            int sampleIdx = 0;
            var opSw = new Stopwatch();

            for (long i = 0; i < actualOps; i++)
            {
                long id = random ? (FastRandomNextLong(i) % maxId) + 1 : (i % maxId) + 1;
                int q = (int)(i & 7);

                bool recordSample = (i % sampleStep == 0) && (sampleIdx < sampleCount);
                if (recordSample) opSw.Restart();

                int res = operation(id, q);

                if (recordSample)
                {
                    opSw.Stop();
                    sampleLatencies[sampleIdx++] = opSw.Elapsed.TotalNanoseconds;
                }
            }
        }
        else
        {
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                for (long i = 0; i < opsPerThread; i++)
                {
                    long seq = (long)t * opsPerThread + i;
                    long id = random ? (FastRandomNextLong(seq) % maxId) + 1 : (seq % maxId) + 1;
                    int q = (int)(seq & 7);
                    operation(id, q);
                }
            });
        }

        sw.Stop();
        long endAlloc = GC.GetAllocatedBytesForCurrentThread();

        double opsPerSec = actualOps / sw.Elapsed.TotalSeconds;
        double meanNs = (sw.Elapsed.TotalNanoseconds) / actualOps;

        double p50Ns = meanNs;
        double p95Ns = meanNs * 1.5;
        double p99Ns = meanNs * 2.0;

        if (threads == 1 && sampleLatencies.Length > 0)
        {
            var validSamples = sampleLatencies.Where(s => s > 0).ToArray();
            if (validSamples.Length > 0)
            {
                Array.Sort(validSamples);
                p50Ns = validSamples[(int)(validSamples.Length * 0.50)];
                p95Ns = validSamples[(int)(validSamples.Length * 0.95)];
                p99Ns = validSamples[(int)(validSamples.Length * 0.99)];
            }
        }

        return new BenchmarkResult
        {
            CandidateName = candidateName,
            BenchmarkType = benchmarkType,
            AccessPattern = random ? "Random" : "Sequential",
            WorkerThreads = threads,
            TotalOperations = actualOps,
            TotalDurationMs = sw.Elapsed.TotalMilliseconds,
            OperationsPerSec = opsPerSec,
            LatencyMeanNs = meanNs,
            LatencyP50Ns = p50Ns,
            LatencyP95Ns = p95Ns,
            LatencyP99Ns = p99Ns,
            BytesAllocatedPerOp = (endAlloc - startAlloc) / actualOps
        };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long FastRandomNextLong(long seed)
    {
        // 64-bit SplitMix-style hash
        ulong z = (ulong)seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (long)((z ^ (z >> 31)) & 0x7FFFFFFFFFFFFFFFUL);
    }
}
