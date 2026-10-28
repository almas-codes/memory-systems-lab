using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NeuroCache.Core;

namespace NeuroCache.EapResearch;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "inventory":
                    return HandleInventory();

                case "database":
                    return await HandleDatabaseAsync(args);

                case "dataset":
                    return await HandleDatasetAsync(args);

                case "eap":
                    return await HandleEapAsync(args);

                case "benchmark":
                    return await HandleBenchmarkAsync(args);

                case "compare":
                    return HandleCompare(args);

                case "report":
                    return HandleReport(args);

                case "adversarial":
                    return await HandleAdversarialAsync(args);

                default:
                    Console.WriteLine($"Unknown command: {cmd}");
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"
NeuroCache.EapResearch CLI
Usage:
  dotnet run --project src/NeuroCache.EapResearch -- inventory
  dotnet run --project src/NeuroCache.EapResearch -- database preflight
  dotnet run --project src/NeuroCache.EapResearch -- dataset generate --records <count> [--unlogged true|false]
  dotnet run --project src/NeuroCache.EapResearch -- dataset verify
  dotnet run --project src/NeuroCache.EapResearch -- eap build --mode bitmap
  dotnet run --project src/NeuroCache.EapResearch -- benchmark run --suite <smoke|eap-500m|updates>
  dotnet run --project src/NeuroCache.EapResearch -- compare --latest
  dotnet run --project src/NeuroCache.EapResearch -- report
");
    }

    private static int HandleInventory()
    {
        var info = HostEnvironmentInfo.Capture();
        Console.WriteLine("==================================================================");
        Console.WriteLine("              EAP HOST & STORAGE ENVIRONMENT                      ");
        Console.WriteLine("==================================================================");
        Console.WriteLine($"OS                  : {info.OsDescription} ({info.Architecture})");
        Console.WriteLine($"Logical Cores       : {info.LogicalCores}");
        Console.WriteLine($".NET Runtime        : {info.FrameworkDescription}");
        Console.WriteLine($"Total RAM           : {info.TotalRamBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Free RAM            : {info.FreeRamBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Storage Volume      : {info.StorageDrive} ({info.StorageDriveType})");
        Console.WriteLine($"Storage Free        : {info.StorageFreeBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine("==================================================================");
        return 0;
    }

    private static async Task<int> HandleDatabaseAsync(string[] args)
    {
        if (args.Length < 2 || args[1].ToLowerInvariant() != "preflight")
        {
            Console.WriteLine("Usage: database preflight");
            return 1;
        }

        Console.WriteLine("Running PostgreSQL Preflight & Capacity Analysis...");
        var report = await PostgreSqlPreflight.RunPreflightAsync();

        Console.WriteLine("==================================================================");
        Console.WriteLine("             POSTGRESQL PREFLIGHT & CAPACITY REPORT               ");
        Console.WriteLine("==================================================================");
        Console.WriteLine($"Server Version      : {report.ServerVersion.Split('\n')[0]}");
        Console.WriteLine($"Database            : {report.CurrentDatabase}");
        Console.WriteLine($"Schema              : {report.CurrentSchema}");
        Console.WriteLine($"Tablespace Name     : {report.TablespaceName} ({report.TablespaceLocation})");
        Console.WriteLine($"Tablespace Free (D:): {report.TablespaceFreeBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"System Drive (C:)   : {report.SystemDriveFreeBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Pilot 100k Table    : {report.PilotTableBytes / (1024 * 1024.0):F2} MB");
        Console.WriteLine($"Pilot 100k Index    : {report.PilotIndexBytes / (1024 * 1024.0):F2} MB");
        Console.WriteLine($"Estimated 500M Table: {report.Estimated500MTableBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Estimated 500M Index: {report.Estimated500MIndexBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Total Required (est): {report.EstimatedTotalRequiredBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Capacity Check      : {(report.CanSafelyHost500M ? "SAFE [PASS]" : "WARNING")}");
        Console.WriteLine($"Recommendation      : {report.Recommendation}");
        Console.WriteLine("==================================================================");
        return report.CanSafelyHost500M ? 0 : 1;
    }

    private static async Task<int> HandleDatasetAsync(string[] args)
    {
        if (args.Length < 2) return 1;
        string sub = args[1].ToLowerInvariant();

        var generator = new EapDatasetGenerator();
        string schemaName = GetArgValue(args, "--schema") ?? "eap_audit_20261028";
        string tableName = GetArgValue(args, "--table") ?? "accounts_10m";

        if (sub == "generate")
        {
            string? countStr = GetArgValue(args, "--records");
            long records = countStr != null ? long.Parse(countStr) : 10_000_000L;
            bool unlogged = bool.Parse(GetArgValue(args, "--unlogged") ?? "true");

            Console.WriteLine($"Generating {records:N0} records in PostgreSQL target '{schemaName}.{tableName}' (Mode: {(unlogged ? "UNLOGGED" : "LOGGED")})...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var manifest = await generator.GenerateAsync(
                records, 
                unlogged: unlogged, 
                schemaName: schemaName, 
                tableName: tableName, 
                progressCallback: (done, total) =>
            {
                double pct = (double)done / total * 100.0;
                double rate = done / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"Progress: {done:N0} / {total:N0} records ({pct:F1}%) at {rate:N0} rec/sec - Elapsed: {sw.Elapsed:mm\\:ss}");
            });

            sw.Stop();
            string manifestDir = Path.Combine(Directory.GetCurrentDirectory(), "results", "audit_20261028");
            string manifestPath = Path.Combine(manifestDir, $"manifest_{schemaName}_{tableName}.json");
            manifest.Save(manifestPath);

            Console.WriteLine("==================================================================");
            Console.WriteLine("              POSTGRESQL DATASET GENERATION COMPLETE              ");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"Target Table        : {schemaName}.{tableName}");
            Console.WriteLine($"Total Rows          : {manifest.RowCount:N0}");
            Console.WriteLine($"Table Footprint     : {manifest.TableBytes / (1024 * 1024 * 1024.0):F2} GB ({manifest.TableBytes / (1024 * 1024.0):F2} MB)");
            Console.WriteLine($"Index Footprint     : {manifest.IndexBytes / (1024 * 1024 * 1024.0):F2} GB ({manifest.IndexBytes / (1024 * 1024.0):F2} MB)");
            Console.WriteLine($"Elapsed Duration    : {sw.Elapsed}");
            Console.WriteLine($"Throughput          : {records / sw.Elapsed.TotalSeconds:N0} rows/sec");
            Console.WriteLine($"Manifest Path       : {manifestPath}");
            Console.WriteLine("==================================================================");
            return 0;
        }
        else if (sub == "verify")
        {
            string? countStr = GetArgValue(args, "--records");
            long records = countStr != null ? long.Parse(countStr) : 10_000_000L;

            Console.WriteLine($"Verifying {records:N0} records in PostgreSQL table '{schemaName}.{tableName}'...");
            bool ok = await generator.VerifyAsync(records, schemaName, tableName);
            Console.WriteLine($"Dataset Verification Status: {(ok ? "VALID [PASS]" : "FAILED")}");
            return ok ? 0 : 1;
        }

        return 1;
    }

    private static async Task<int> HandleEapAsync(string[] args)
    {
        if (args.Length < 2 || args[1].ToLowerInvariant() != "build") return 1;

        string? countStr = GetArgValue(args, "--records");
        long records = countStr != null ? long.Parse(countStr) : 10_000_000L;
        string tableName = GetArgValue(args, "--table") ?? "eap_audit_20261028.accounts_10m";
        string mode = (GetArgValue(args, "--mode") ?? "all").ToLowerInvariant();
        string connStr = PostgreSqlPreflight.GetConnectionString();

        Console.WriteLine($"Hydrating EAP Planes for {records:N0} accounts from table '{tableName}'...");

        if (mode is "bitmap" or "all")
        {
            var bitmap = new EapBooleanBitmap(records);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var duration = await bitmap.BuildFromPostgreSqlAsync(connStr, threshold: 100, tableName: tableName, progressCallback: (done, total) =>
            {
                if (done % 2_000_000 == 0 || done == total)
                    Console.WriteLine($"Building EAP Bitmap: {done:N0} / {total:N0} accounts streamed...");
            });
            sw.Stop();

            Console.WriteLine("==================================================================");
            Console.WriteLine("                  EAP BITMAP BUILD COMPLETE                       ");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"Represented Records : {records:N0}");
            Console.WriteLine($"Raw Bitmap Memory   : {bitmap.RawMemoryBytes / (1024 * 1024.0):F2} MB ({bitmap.RawMemoryBytes / (1024.0 * 1024.0 * 1.048576):F2} MiB)");
            Console.WriteLine($"Bytes Per Record    : {(double)bitmap.RawMemoryBytes / records:F3} bytes (0.125 bytes/record)");
            Console.WriteLine($"Build Duration      : {sw.Elapsed}");
            Console.WriteLine($"Build Rate          : {records / sw.Elapsed.TotalSeconds:N0} accounts/sec");
            Console.WriteLine("==================================================================");
        }

        if (mode is "category" or "all")
        {
            var category = new EapPackedCategoryPlane(records);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var duration = await category.BuildFromPostgreSqlAsync(connStr, tableName: tableName, progressCallback: (done, total) =>
            {
                if (done % 2_000_000 == 0 || done == total)
                    Console.WriteLine($"Building EAP Category Plane: {done:N0} / {total:N0} accounts streamed...");
            });
            sw.Stop();

            Console.WriteLine("==================================================================");
            Console.WriteLine("              EAP PACKED CATEGORY BUILD COMPLETE                  ");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"Represented Records : {records:N0}");
            Console.WriteLine($"Raw Category Memory : {category.RawMemoryBytes / (1024 * 1024.0):F2} MB ({category.RawMemoryBytes / (1024.0 * 1024.0 * 1.048576):F2} MiB)");
            Console.WriteLine($"Bytes Per Record    : {(double)category.RawMemoryBytes / records:F3} bytes (0.625 bytes/record)");
            Console.WriteLine($"Build Duration      : {sw.Elapsed}");
            Console.WriteLine($"Build Rate          : {records / sw.Elapsed.TotalSeconds:N0} accounts/sec");
            Console.WriteLine("==================================================================");
        }

        return 0;
    }

    private static async Task<int> HandleBenchmarkAsync(string[] args)
    {
        if (args.Length < 2 || args[1].ToLowerInvariant() != "run") return 1;

        string suite = GetArgValue(args, "--suite") ?? "audit-master";
        string? countStr = GetArgValue(args, "--records");
        long records = countStr != null ? long.Parse(countStr) : 1_000_000L;
        string tableName = GetArgValue(args, "--table") ?? "eap_lab.accounts_500m";
        string outDir = GetArgValue(args, "--outdir") ?? Path.Combine(Directory.GetCurrentDirectory(), "results", "audit_20261028");
        int reps = int.Parse(GetArgValue(args, "--repetitions") ?? "5");
        int reqs = int.Parse(GetArgValue(args, "--requests") ?? "2500");
        string connStr = PostgreSqlPreflight.GetConnectionString();

        Console.WriteLine($"Executing EAP Benchmark Suite '{suite}' on table '{tableName}' (Dataset Scale: {records:N0}, Repetitions: {reps}, Requests/rep: {reqs:N0})...");
        var results = new List<EapBenchmarkResult>();

        var candidateFactories = new Func<IEapBenchmarkCandidate>[]
        {
            () => new Candidate1_PostgreSqlDirect(),
            () => new Candidate2_PostgreSqlCovering(),
            () => new Candidate3_BoundedResultCache(128 * 1024 * 1024),
            () => new Candidate4_WarrantV4(4_194_304),
            () => new Candidate5_EapBooleanBitmap(),
            () => new Candidate6_EapPackedCategory()
        };

        var workloads = new (string Name, double ReadPct, bool Zipf, bool Churn)[]
        {
            ("ReadOnly-100", 100.0, false, false),
            ("ReadHeavy-99-1", 99.0, true, false),
            ("ReadHeavy-95-5", 95.0, true, false),
            ("Balanced-80-20", 80.0, true, false),
            ("WriteIntensive-50-50", 50.0, true, false),
            ("Churn-Boundary", 50.0, true, true)
        };

        foreach (var factory in candidateFactories)
        {
            var candidate = factory();
            Console.WriteLine($"\n--- Initializing candidate: {candidate.Name} (Table: {tableName}) ---");
            await candidate.InitializeAsync(records, connStr, tableName: tableName);

            foreach (var w in workloads)
            {
                // Isolate candidate cache between workloads to eliminate cross-workload contamination
                candidate.ResetCache();

                // Warmup run (discarded)
                await EapBenchmarkRunner.RunBenchmarkAsync(candidate, w.Name, records, totalRequests: Math.Min(reqs, 500), readPercent: w.ReadPct, zipfian: w.Zipf, boundaryChurn: w.Churn, repetition: 0);

                // Measured repetitions
                for (int r = 1; r <= reps; r++)
                {
                    var res = await EapBenchmarkRunner.RunBenchmarkAsync(
                        candidate, 
                        w.Name, 
                        records, 
                        totalRequests: reqs, 
                        readPercent: w.ReadPct, 
                        zipfian: w.Zipf, 
                        boundaryChurn: w.Churn, 
                        repetition: r);

                    results.Add(res);
                    Console.WriteLine($"[{candidate.Name,-22}] {w.Name,-20} Rep {r}/{reps}: {res.ThroughputOpsPerSec,10:N0} ops/s, p50={res.P50LatencyUs,6:F1}us, p99={res.P99LatencyUs,6:F1}us, Alloc={res.AllocBytesPerOp:F1}B");
                }
            }

            await candidate.DisposeAsync();
        }

        EapBenchmarkRunner.SaveResults(results, outDir);
        Console.WriteLine($"\nBenchmark complete. Saved {results.Count} results to {outDir}");
        PrintSummaryTable(results);
        return 0;
    }

    private static void PrintSummaryTable(List<EapBenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("| Candidate | Workload | Throughput (ops/s) | Comb p50 (us) | Read p50 (us) | Write p50 (us) | p95 (us) | p99 (us) | Reads / Writes | Resident RAM |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            string rw = $"{r.CompletedReads:N0}R/{r.CompletedWrites:N0}W";
            Console.WriteLine($"| {r.CandidateName,-22} | {r.WorkloadName,-20} | {r.ThroughputOpsPerSec,14:N0} | {r.P50LatencyUs,8:F1} | {r.ReadP50LatencyUs,8:F1} | {r.WriteP50LatencyUs,8:F1} | {r.P95LatencyUs,8:F1} | {r.P99LatencyUs,8:F1} | {rw,12} | {r.ResidentBytes / (1024 * 1024.0),8:F1} MB |");
        }
        Console.WriteLine();
    }

    private static int HandleCompare(string[] args)
    {
        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "results", "eap-500m");
        string jsonPath = Path.Combine(outDir, "eap_benchmark_results.json");
        if (!File.Exists(jsonPath))
        {
            Console.WriteLine("No EAP results found. Run benchmark run first.");
            return 1;
        }

        var list = JsonSerializer.Deserialize<List<EapBenchmarkResult>>(File.ReadAllText(jsonPath));
        if (list != null) PrintSummaryTable(list);
        return 0;
    }

    private static int HandleReport(string[] args)
    {
        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "results", "eap-500m");
        string reportPath = Path.Combine(Directory.GetCurrentDirectory(), "docs", "EAP-500M-REPORT.md");
        EapReportGenerator.GenerateReport(outDir, reportPath);
        Console.WriteLine($"EAP Research Report generated at: {reportPath}");
        return 0;
    }

    private static async Task<int> HandleAdversarialAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: adversarial <explain|write-breakdown|run-benchmarks>");
            return 1;
        }

        string sub = args[1].ToLowerInvariant();
        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "results", "adversarial_20261028");
        Directory.CreateDirectory(outDir);
        string connStr = PostgreSqlPreflight.GetConnectionString();

        if (sub == "explain")
        {
            Console.WriteLine("Executing EXPLAIN (ANALYZE, BUFFERS) for Candidates 1 and 2...");
            await using var conn = new Npgsql.NpgsqlConnection(connStr);
            await conn.OpenAsync();

            string q1 = "EXPLAIN (ANALYZE, BUFFERS, VERBOSE) SELECT (balance >= 100 AND active AND version >= 0) FROM eap_lab.accounts_500m WHERE id = 42;";
            string q2 = "EXPLAIN (ANALYZE, BUFFERS, VERBOSE) SELECT (balance >= 100 AND active) FROM eap_lab.accounts_500m WHERE id = 42;";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== CANDIDATE 1: DIRECT QUERY (Heap Fetch) ===");
            await using (var cmd = new Npgsql.NpgsqlCommand(q1, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    string line = r.GetString(0);
                    Console.WriteLine(line);
                    sb.AppendLine(line);
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== CANDIDATE 2: COVERING INDEX CANDIDATE ===");
            await using (var cmd = new Npgsql.NpgsqlCommand(q2, conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    string line = r.GetString(0);
                    Console.WriteLine(line);
                    sb.AppendLine(line);
                }
            }

            File.WriteAllText(Path.Combine(outDir, "explain_plans.txt"), sb.ToString());
            Console.WriteLine($"\nSaved execution plans to {Path.Combine(outDir, "explain_plans.txt")}");
            return 0;
        }

        if (sub == "write-breakdown")
        {
            Console.WriteLine("Profiling write-latency component breakdown (500 writes)...");
            await using (var setupConn = new Npgsql.NpgsqlConnection(connStr))
            {
                await setupConn.OpenAsync();
                await using var createCmd = new Npgsql.NpgsqlCommand(@"
                    CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                    CREATE TABLE IF NOT EXISTS eap_audit_20261028.mutation_log (
                        seq_id BIGSERIAL PRIMARY KEY,
                        account_id BIGINT NOT NULL,
                        balance INT NOT NULL,
                        active BOOLEAN NOT NULL,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                    );", setupConn);
                await createCmd.ExecuteNonQueryAsync();
            }

            var coord = new EapCoordinator(1_000_000, connStr);

            var checkoutUs = new List<double>();
            var txBeginUs = new List<double>();
            var advLockUs = new List<double>();
            var updateUs = new List<double>();
            var outboxUs = new List<double>();
            var commitUs = new List<double>();
            var seqlockUs = new List<double>();
            var lockHeldUs = new List<double>();
            var totalUs = new List<double>();

            int iterations = 500;
            var rng = new Random(42);

            for (int i = 0; i < iterations; i++)
            {
                long id = rng.NextInt64(1, 100_000);
                int bal = rng.Next(50, 150);
                bool act = true;

                long tTotal0 = System.Diagnostics.Stopwatch.GetTimestamp();

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await using var conn = new Npgsql.NpgsqlConnection(connStr);
                await conn.OpenAsync();
                long tCheckout = System.Diagnostics.Stopwatch.GetTimestamp();

                long tTx0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await using var tx = await conn.BeginTransactionAsync();
                long tTx1 = System.Diagnostics.Stopwatch.GetTimestamp();

                long tLock0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await using (var advCmd = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('eap_outbox_lock'));", conn, tx))
                {
                    await advCmd.ExecuteNonQueryAsync();
                }
                long tLock1 = System.Diagnostics.Stopwatch.GetTimestamp();

                long tUp0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await using (var upCmd = new Npgsql.NpgsqlCommand("UPDATE eap_lab.accounts_500m SET balance = @b, active = @a, version = version + 1 WHERE id = @id;", conn, tx))
                {
                    upCmd.Parameters.AddWithValue("b", bal);
                    upCmd.Parameters.AddWithValue("a", act);
                    upCmd.Parameters.AddWithValue("id", id);
                    await upCmd.ExecuteNonQueryAsync();
                }
                long tUp1 = System.Diagnostics.Stopwatch.GetTimestamp();

                long tOut0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await using (var outCmd = new Npgsql.NpgsqlCommand("INSERT INTO eap_audit_20261028.mutation_log (account_id, balance, active) VALUES (@id, @b, @a) RETURNING seq_id;", conn, tx))
                {
                    outCmd.Parameters.AddWithValue("id", id);
                    outCmd.Parameters.AddWithValue("b", bal);
                    outCmd.Parameters.AddWithValue("a", act);
                    await outCmd.ExecuteScalarAsync();
                }
                long tOut1 = System.Diagnostics.Stopwatch.GetTimestamp();

                long tCom0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await tx.CommitAsync();
                long tCom1 = System.Diagnostics.Stopwatch.GetTimestamp();

                long tSeq0 = System.Diagnostics.Stopwatch.GetTimestamp();
                coord.PublishCrossPlane(id, bal, act);
                long tSeq1 = System.Diagnostics.Stopwatch.GetTimestamp();

                long tTotal1 = System.Diagnostics.Stopwatch.GetTimestamp();

                double freq = System.Diagnostics.Stopwatch.Frequency / 1_000_000.0;
                checkoutUs.Add((tCheckout - t0) / freq);
                txBeginUs.Add((tTx1 - tTx0) / freq);
                advLockUs.Add((tLock1 - tLock0) / freq);
                updateUs.Add((tUp1 - tUp0) / freq);
                outboxUs.Add((tOut1 - tOut0) / freq);
                commitUs.Add((tCom1 - tCom0) / freq);
                seqlockUs.Add((tSeq1 - tSeq0) / freq);
                lockHeldUs.Add((tCom1 - tLock1) / freq); // Lock held duration from acquisition to commit
                totalUs.Add((tTotal1 - tTotal0) / freq);
            }

            await coord.DisposeAsync();

            var summary = new
            {
                ConnCheckout = ComputeStats(checkoutUs),
                TxBegin = ComputeStats(txBeginUs),
                AdvisoryLockAcquisition = ComputeStats(advLockUs),
                PostgresUpdate = ComputeStats(updateUs),
                OutboxInsert = ComputeStats(outboxUs),
                TxCommit = ComputeStats(commitUs),
                SeqlockPublish = ComputeStats(seqlockUs),
                LockHeldDuration = ComputeStats(lockHeldUs),
                TotalWrite = ComputeStats(totalUs)
            };

            double componentSum = summary.ConnCheckout.Mean + summary.TxBegin.Mean + summary.AdvisoryLockAcquisition.Mean + 
                                  summary.PostgresUpdate.Mean + summary.OutboxInsert.Mean + summary.TxCommit.Mean + 
                                  summary.SeqlockPublish.Mean;
            double unmeasuredGap = summary.TotalWrite.Mean - componentSum;

            string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outDir, "write_breakdown.json"), json);

            Console.WriteLine("==================================================================================");
            Console.WriteLine("                     WRITE LATENCY COMPONENT BREAKDOWN (us)                      ");
            Console.WriteLine("==================================================================================");
            Console.WriteLine($"{"Component",-24} | {"Mean",8} | {"P50",8} | {"P95",8} | {"P99",8} | {"Max",8} | {"Fraction",8}");
            Console.WriteLine("-------------------------|----------|----------|----------|----------|----------|---------");
            PrintComponent("Conn Checkout", summary.ConnCheckout, summary.TotalWrite.Mean);
            PrintComponent("Tx Begin", summary.TxBegin, summary.TotalWrite.Mean);
            PrintComponent("Advisory Lock Acquire", summary.AdvisoryLockAcquisition, summary.TotalWrite.Mean);
            PrintComponent("PG UPDATE", summary.PostgresUpdate, summary.TotalWrite.Mean);
            PrintComponent("Outbox INSERT", summary.OutboxInsert, summary.TotalWrite.Mean);
            PrintComponent("TX Commit (WAL)", summary.TxCommit, summary.TotalWrite.Mean);
            PrintComponent("Seqlock Publish", summary.SeqlockPublish, summary.TotalWrite.Mean);
            Console.WriteLine($"{"Unmeasured Gap / Glue",-24} | {unmeasuredGap,8:F1} | {"N/A",8} | {"N/A",8} | {"N/A",8} | {"N/A",8} | {unmeasuredGap / summary.TotalWrite.Mean * 100.0,7:F1}%");
            Console.WriteLine("-------------------------|----------|----------|----------|----------|----------|---------");
            PrintComponent("TOTAL WALL CLOCK", summary.TotalWrite, summary.TotalWrite.Mean);
            Console.WriteLine("-------------------------|----------|----------|----------|----------|----------|---------");
            PrintComponent("LOCK HELD DURATION", summary.LockHeldDuration, summary.TotalWrite.Mean);
            Console.WriteLine("==================================================================================");
            Console.WriteLine($"Component Sum: {componentSum:F1} us | Total Wall Clock: {summary.TotalWrite.Mean:F1} us | Reconciliation Gap: {unmeasuredGap:F1} us");
            Console.WriteLine($"Lock is held for {summary.LockHeldDuration.Mean:F1} us (~{summary.LockHeldDuration.Mean / 1000.0:F2} ms) per transaction.");
            Console.WriteLine($"Max theoretical serial write throughput through global lock: {1_000_000.0 / summary.LockHeldDuration.Mean:F0} writes/sec.");
            Console.WriteLine($"Saved write breakdown telemetry to {Path.Combine(outDir, "write_breakdown.json")}");
            return 0;
        }

        if (sub == "verify-category")
        {
            Console.WriteLine("Running comprehensive 1,000,000-record category semantics verification against PostgreSQL...");
            int[] testThresholds = { 0, 1, 12, 13, 26, 50, 100, 124, 125, 129, 130, 194, 195, 196, 208 };
            long maxId = 1_000_000L;

            var plane = new EapPackedCategoryPlane(maxId);
            Console.WriteLine("Hydrating category plane from PostgreSQL eap_lab.accounts_500m...");
            await plane.BuildFromPostgreSqlAsync(connStr, "eap_lab.accounts_500m");

            Console.WriteLine("Streaming ground truth from PostgreSQL and evaluating category plane...");
            await using var conn = new Npgsql.NpgsqlConnection(connStr);
            await conn.OpenAsync();

            var resultsList = new List<object>();

            Console.WriteLine();
            Console.WriteLine("| Threshold | Exact? | Total Records | Definite True | Definite False | Ambiguous | Fallback Req | Fallback % | Discrepancies |");
            Console.WriteLine("|---|---|---|---|---|---|---|---|---|");

            foreach (int t in testThresholds)
            {
                bool isExact = EapPackedCategoryPlane.IsThresholdExact(t);
                long total = 0;
                long defTrue = 0;
                long defFalse = 0;
                long ambiguous = 0;
                long discrepancies = 0;

                string query = "SELECT id, balance, active FROM eap_lab.accounts_500m ORDER BY id ASC;";
                await using var cmd = new Npgsql.NpgsqlCommand(query, conn);
                cmd.CommandTimeout = 1800;
                await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess);

                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(0);
                    int balance = reader.GetInt32(1);
                    bool active = reader.GetBoolean(2);

                    bool dbGroundTruth = (balance >= t) && active;
                    var eval = plane.EvaluateThresholdDetailed(id, t);

                    total++;
                    if (eval == CategoryEvaluationResult.DefiniteTrue)
                    {
                        defTrue++;
                        if (!dbGroundTruth) discrepancies++;
                    }
                    else if (eval == CategoryEvaluationResult.DefiniteFalse)
                    {
                        defFalse++;
                        if (dbGroundTruth) discrepancies++;
                    }
                    else // AmbiguousBucket
                    {
                        ambiguous++;
                    }
                }

                double fallbackPct = total > 0 ? (double)ambiguous / total * 100.0 : 0.0;
                string exactStr = isExact ? "YES" : "NO";
                Console.WriteLine($"| {t,9} | {exactStr,6} | {total,13:N0} | {defTrue,13:N0} | {defFalse,14:N0} | {ambiguous,9:N0} | {ambiguous,12:N0} | {fallbackPct,9:F2}% | {discrepancies,13} |");

                resultsList.Add(new
                {
                    Threshold = t,
                    IsExact = isExact,
                    TotalRecords = total,
                    DefiniteTrue = defTrue,
                    DefiniteFalse = defFalse,
                    Ambiguous = ambiguous,
                    FallbackCount = ambiguous,
                    FallbackPercentage = fallbackPct,
                    Discrepancies = discrepancies
                });
            }

            string catJson = JsonSerializer.Serialize(resultsList, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outDir, "category_semantics_audit.json"), catJson);
            Console.WriteLine($"\nSaved category semantics audit to {Path.Combine(outDir, "category_semantics_audit.json")}");
            return 0;
        }

        if (sub == "run-benchmarks")
        {
            string tableName = GetArgValue(args, "--table") ?? "eap_lab.accounts_500m";
            long records = 1_000_000L;
            int reps = int.Parse(GetArgValue(args, "--repetitions") ?? "3");
            int reqs = int.Parse(GetArgValue(args, "--requests") ?? "5000");

            Console.WriteLine($"Running authoritative adversarial benchmark on '{tableName}' ({records:N0} records, {reps} reps, {reqs} reqs/rep)...");
            var benchmarkArgs = new string[]
            {
                "benchmark", "run",
                "--records", records.ToString(),
                "--table", tableName,
                "--repetitions", reps.ToString(),
                "--requests", reqs.ToString(),
                "--outdir", outDir
            };
            return await HandleBenchmarkAsync(benchmarkArgs);
        }

        Console.WriteLine($"Unknown adversarial subcommand: {sub}");
        return 1;
    }

    public class ComponentStat
    {
        public double Mean { get; set; }
        public double StdDev { get; set; }
        public double P50 { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
    }

    private static void PrintComponent(string name, ComponentStat stat, double totalMean)
    {
        double frac = stat.Mean / totalMean * 100.0;
        Console.WriteLine($"{name,-20} | {stat.Mean,8:F1} | {stat.P50,8:F1} | {stat.P95,8:F1} | {stat.P99,8:F1} | {stat.Max,8:F1} | {frac,7:F1}%");
    }

    private static ComponentStat ComputeStats(List<double> values)
    {
        values.Sort();
        double sum = 0;
        foreach (var v in values) sum += v;
        double mean = sum / values.Count;

        double sumSq = 0;
        foreach (var v in values) sumSq += (v - mean) * (v - mean);
        double stdDev = Math.Sqrt(sumSq / values.Count);

        double p50 = values[(int)(values.Count * 0.50)];
        double p95 = values[(int)(values.Count * 0.95)];
        double p99 = values[(int)(values.Count * 0.99)];
        double min = values[0];
        double max = values[^1];

        return new ComponentStat { Mean = mean, StdDev = stdDev, P50 = p50, P95 = p95, P99 = p99, Min = min, Max = max };
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
