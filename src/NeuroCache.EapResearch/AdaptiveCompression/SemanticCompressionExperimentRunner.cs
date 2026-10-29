using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch.AdaptiveCompression;

public sealed class SemanticCompressionExperimentRunner
{
    private readonly string _connectionString;
    private readonly string _outputDir;
    private readonly long _maxId;

    public SemanticCompressionExperimentRunner(
        string? connectionString = null,
        string? outputDir = null,
        long maxId = 50_000_000)
    {
        _connectionString = connectionString ?? PostgreSqlPreflight.GetConnectionString();
        _outputDir = outputDir ?? Path.Combine(Directory.GetCurrentDirectory(), "results", "semantic_compression_20261029");
        Directory.CreateDirectory(_outputDir);
        _maxId = maxId;
    }

    public async Task RunAllAsync(CancellationToken ct = default)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  NEUROCACHE RESEARCHLAB — ADAPTIVE SEMANTIC COMPRESSION EXPERIMENT             ");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"Target Dataset : eap_audit_20261028.accounts_50m ({_maxId:N0} records)");
        Console.WriteLine($"Output Directory: {_outputDir}");
        Console.WriteLine();

        // 1. Phase 0 Security & Preflight Check
        Console.WriteLine("[Step 1/6] Provisioning Least-Privilege Roles & Running Security Boundary Audit...");
        await PostgreSqlPreflight.EnsureLeastPrivilegeRolesAsync(_connectionString, ct);
        string appConnStr = PostgreSqlPreflight.GetAppConnectionString();
        string writerConnStr = PostgreSqlPreflight.GetWriterConnectionString();
        Console.WriteLine($"  Read Role  : neurocache_app (Conn: {PostgreSqlPreflight.SanitizeConnectionString(appConnStr)})");
        Console.WriteLine($"  Write Role : neurocache_writer (Conn: {PostgreSqlPreflight.SanitizeConnectionString(writerConnStr)})");

        // 2. Phase 1 Profile Pattern Distribution on Live 50M Dataset
        Console.WriteLine("\n[Step 2/6] Profiling Real 50M Query Pattern Distribution...");
        var patternDistribution = await ProfilePatternDistributionAsync(appConnStr, ct);
        File.WriteAllText(
            Path.Combine(_outputDir, "pattern_distribution_50m.json"),
            JsonSerializer.Serialize(patternDistribution, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Identified {patternDistribution.Count} distinct answer patterns across 50,000,000 records.");
        foreach (var p in patternDistribution)
        {
            Console.WriteLine($"    Pattern 0x{p.Pattern:X2} (b{Convert.ToString(p.Pattern, 2).PadLeft(8, '0')}): {p.Frequency:N0} records ({p.Percentage:F2}%) - {p.SemanticDescription}");
        }

        // 3. Phase 2 & 5 Memory Footprint Audit
        Console.WriteLine("\n[Step 3/6] Allocating Candidates & Auditing Exact Memory Footprints...");
        var candA = new CandidateA_EightIndependentBitmaps(_maxId);
        var candB = new CandidateB_PackedByteAnswerPattern(_maxId);
        var candC = new CandidateC_SemanticAnswerClassPlane(_maxId);
        var candD = new CandidateD_ReferenceSingleBitmap(_maxId);
        var candE = new CandidateE_PostgreSqlBaseline(appConnStr);

        var memAudit = AuditMemoryFootprints(candA, candB, candC, candD);
        File.WriteAllText(
            Path.Combine(_outputDir, "memory_footprint_audit.json"),
            JsonSerializer.Serialize(memAudit, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var m in memAudit)
        {
            Console.WriteLine($"  {m.Candidate}: {m.Bytes:N0} bytes | {m.DecimalMb:F2} MB (decimal) | {m.BinaryMib:F2} MiB (binary) | {m.Notes}");
        }

        // 4. Synchronized Hydration from PostgreSQL 50M Table
        Console.WriteLine("\n[Step 4/6] Streaming 50,000,000 Accounts from PostgreSQL into Candidates...");
        var swHydrate = Stopwatch.StartNew();
        long hydratedCount = await HydrateCandidatesFromPostgreSqlAsync(appConnStr, candA, candB, candC, candD, ct);
        swHydrate.Stop();
        Console.WriteLine($"\n  Hydrated {hydratedCount:N0} accounts into memory in {swHydrate.Elapsed.TotalSeconds:F2}s ({hydratedCount / swHydrate.Elapsed.TotalSeconds:N0} rows/s).");
        Console.WriteLine($"  Candidate C registered {candC.DistinctClassCount} semantic classes in memory dictionary.");

        // Verification of consistency across 100,000 accounts
        Console.WriteLine("  Verifying 100% exact parity across candidates...");
        SemanticCompressionUpdateRunner.VerifyCorrectnessAcrossAllCandidates(candA, candB, candC, 100_000);
        Console.WriteLine("  Parity check PASS: 100,000 accounts verified across all 8 queries (0 mismatches).");

        // 5. Phase 3 Random-Access & Concurrent Benchmark Suite
        Console.WriteLine("\n[Step 5/6] Executing Comprehensive Benchmark Suite across 1, 4, 8, 16 Threads...");
        var suite = new SemanticCompressionBenchmarkSuite(candA, candB, candC, candD, candE, _maxId);
        var benchmarkResults = suite.RunAllMemoryBenchmarks(singleThreadOps: 15_000_000, multiThreadOps: 30_000_000);

        Console.WriteLine("  Running PostgreSQL baseline latency samples...");
        var pgResults = await suite.RunPostgreSqlBaselinesAsync(sampleOps: 500);
        benchmarkResults.AddRange(pgResults);

        // Export Benchmark Matrix
        ExportBenchmarkResults(benchmarkResults);

        // 6. Phase 4 Incremental Updates & Class Transitions
        Console.WriteLine("\n[Step 6/6] Executing Incremental Updates, Dynamic Class Registration & CAS Tests...");
        SemanticCompressionUpdateRunner.VerifyDynamicClassRegistration(candC);
        Console.WriteLine("  Dynamic class registration: PASS (Novel pattern dynamically discovered and registered).");

        SemanticCompressionUpdateRunner.VerifyAdjacentIdConcurrentUpdatesUnderCas(candC);
        Console.WriteLine("  Adjacent-ID CAS concurrency: PASS (No torn nibbles between even/odd account IDs sharing byte).");

        Console.WriteLine("  Benchmarking incremental update throughput (2,000,000 mutations per candidate)...");
        var upA = SemanticCompressionUpdateRunner.BenchmarkUpdatesCandidateA(candA, 2_000_000);
        var upB = SemanticCompressionUpdateRunner.BenchmarkUpdatesCandidateB(candB, 2_000_000);
        var upC = SemanticCompressionUpdateRunner.BenchmarkUpdatesCandidateC(candC, 2_000_000);

        var updateResults = new[] { upA, upB, upC };
        File.WriteAllText(
            Path.Combine(_outputDir, "update_benchmark.json"),
            JsonSerializer.Serialize(updateResults, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var u in updateResults)
        {
            Console.WriteLine($"    {u.CandidateName}: {u.UpdatesPerSec:N0} updates/sec (Mean latency: {u.LatencyMeanNs:F2} ns)");
        }

        // Summary report
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("        ADAPTIVE SEMANTIC COMPRESSION EXPERIMENT COMPLETED                      ");
        Console.WriteLine($"All telemetry, CSVs, and JSON artifacts written to: {_outputDir}");
        Console.WriteLine("================================================================================");
    }

    private async Task<List<PatternDistributionRecord>> ProfilePatternDistributionAsync(
        string appConnStr,
        CancellationToken ct)
    {
        var list = new List<PatternDistributionRecord>();
        await using var conn = new NpgsqlConnection(appConnStr);
        await conn.OpenAsync(ct);

        string sql = @"
            SELECT 
                ((active::int << 7) | 
                 (((balance >= 100 AND active)::int) << 6) |
                 (((balance >= 250 AND active)::int) << 5) |
                 (((balance >= 500 AND active)::int) << 4) |
                 (((balance >= 750 AND active)::int) << 3) |
                 (((balance >= 900 AND active)::int) << 2) |
                 (((balance >= 500)::int) << 1) |
                 (((balance >= 100)::int) << 0)) AS pattern,
                count(*) AS freq
            FROM eap_audit_20261028.accounts_50m
            GROUP BY 1
            ORDER BY freq DESC;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        long totalRows = 0;
        var rawRows = new List<(byte pat, long count)>();
        while (await reader.ReadAsync(ct))
        {
            byte pat = Convert.ToByte(reader.GetInt32(0));
            long count = reader.GetInt64(1);
            totalRows += count;
            rawRows.Add((pat, count));
        }

        foreach (var (pat, count) in rawRows)
        {
            list.Add(new PatternDistributionRecord
            {
                Pattern = pat,
                Frequency = count,
                Percentage = (double)count / totalRows * 100.0,
                SemanticDescription = DescribePattern(pat)
            });
        }

        return list;
    }

    private static string DescribePattern(byte p)
    {
        bool active = (p & 0x80) != 0;
        if (!active)
        {
            if ((p & 0x02) != 0) return "Inactive, balance >= 500";
            if ((p & 0x01) != 0) return "Inactive, 100 <= balance < 500";
            return "Inactive, balance < 100";
        }

        if ((p & 0x04) != 0) return "Active, balance >= 900 (High Tier)";
        if ((p & 0x08) != 0) return "Active, 750 <= balance < 900 (Upper Tier)";
        if ((p & 0x10) != 0) return "Active, 500 <= balance < 750 (Mid Tier)";
        if ((p & 0x20) != 0) return "Active, 250 <= balance < 500 (Standard Tier)";
        if ((p & 0x40) != 0) return "Active, 100 <= balance < 250 (Basic Tier)";
        return "Active, balance < 100 (Micro Tier)";
    }

    private List<MemoryFootprintRecord> AuditMemoryFootprints(
        CandidateA_EightIndependentBitmaps a,
        CandidateB_PackedByteAnswerPattern b,
        CandidateC_SemanticAnswerClassPlane c,
        CandidateD_ReferenceSingleBitmap d)
    {
        return new List<MemoryFootprintRecord>
        {
            new()
            {
                Candidate = "Candidate A (8 Independent Bitmaps)",
                Bytes = a.TotalAllocatedBytes,
                DecimalMb = a.TotalAllocatedBytes / 1_000_000.0,
                BinaryMib = a.TotalAllocatedBytes / (1024.0 * 1024.0),
                Notes = "8 separate 1-bit boolean bitmaps (6.25 MB each)"
            },
            new()
            {
                Candidate = "Candidate B (Packed 8-bit Byte Pattern)",
                Bytes = b.TotalAllocatedBytes,
                DecimalMb = b.TotalAllocatedBytes / 1_000_000.0,
                BinaryMib = b.TotalAllocatedBytes / (1024.0 * 1024.0),
                Notes = "1 contiguous byte per account"
            },
            new()
            {
                Candidate = "Candidate C (Semantic Answer-Class Plane)",
                Bytes = c.TotalAllocatedBytes,
                DecimalMb = c.TotalAllocatedBytes / 1_000_000.0,
                BinaryMib = c.TotalAllocatedBytes / (1024.0 * 1024.0),
                Notes = "4-bit nibbles (2 accounts/byte, 25.0 MB) + 272-byte dictionary"
            },
            new()
            {
                Candidate = "Candidate D (Reference Single Bitmap)",
                Bytes = d.TotalAllocatedBytes,
                DecimalMb = d.TotalAllocatedBytes / 1_000_000.0,
                BinaryMib = d.TotalAllocatedBytes / (1024.0 * 1024.0),
                Notes = "Single 1-bit boolean bitmap (answers 1 query only)"
            }
        };
    }

    private async Task<long> HydrateCandidatesFromPostgreSqlAsync(
        string appConnStr,
        CandidateA_EightIndependentBitmaps a,
        CandidateB_PackedByteAnswerPattern b,
        CandidateC_SemanticAnswerClassPlane c,
        CandidateD_ReferenceSingleBitmap d,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(appConnStr);
        await conn.OpenAsync(ct);

        string sql = "SELECT id, balance, active FROM eap_audit_20261028.accounts_50m;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 1800;

        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);

        long count = 0;
        while (await reader.ReadAsync(ct))
        {
            long id = reader.GetInt64(0);
            int bal = reader.GetInt32(1);
            bool act = reader.GetBoolean(2);

            byte pattern = QueryFamily.ComputeAnswerPattern(bal, act);
            a.SetExplicitPattern(id, pattern);
            b.SetExplicitPattern(id, pattern);
            c.SetExplicitPattern(id, pattern);
            d.Update(id, act);

            count++;
            if (count % 2_500_000 == 0)
            {
                Console.Write($"\r    Hydrating accounts: {count:N0} / {_maxId:N0} ({(count * 100.0 / _maxId):F1}%)...");
            }
        }

        return count;
    }

    private void ExportBenchmarkResults(List<BenchmarkResult> results)
    {
        // 1. JSON
        File.WriteAllText(
            Path.Combine(_outputDir, "benchmark_matrix.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        // 2. CSV
        using var sw = new StreamWriter(Path.Combine(_outputDir, "benchmark_matrix.csv"));
        sw.WriteLine("CandidateName,BenchmarkType,AccessPattern,WorkerThreads,TotalOperations,TotalDurationMs,OperationsPerSec,LatencyMeanNs,LatencyP50Ns,LatencyP95Ns,LatencyP99Ns,BytesAllocatedPerOp");
        foreach (var r in results)
        {
            sw.WriteLine($"\"{r.CandidateName}\",\"{r.BenchmarkType}\",\"{r.AccessPattern}\",{r.WorkerThreads},{r.TotalOperations},{r.TotalDurationMs:F3},{r.OperationsPerSec:F2},{r.LatencyMeanNs:F2},{r.LatencyP50Ns:F2},{r.LatencyP95Ns:F2},{r.LatencyP99Ns:F2},{r.BytesAllocatedPerOp}");
        }
    }
}

public sealed class PatternDistributionRecord
{
    public byte Pattern { get; set; }
    public long Frequency { get; set; }
    public double Percentage { get; set; }
    public string SemanticDescription { get; set; } = "";
}

public sealed class MemoryFootprintRecord
{
    public string Candidate { get; set; } = "";
    public long Bytes { get; set; }
    public double DecimalMb { get; set; }
    public double BinaryMib { get; set; }
    public string Notes { get; set; } = "";
}
