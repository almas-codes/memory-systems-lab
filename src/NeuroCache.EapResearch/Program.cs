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

        if (sub == "generate")
        {
            string? countStr = GetArgValue(args, "--records");
            long records = countStr != null ? long.Parse(countStr) : 500_000_000L;
            bool unlogged = bool.Parse(GetArgValue(args, "--unlogged") ?? "true");

            Console.WriteLine($"Generating {records:N0} records in PostgreSQL (Mode: {(unlogged ? "UNLOGGED" : "LOGGED")})...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var manifest = await generator.GenerateAsync(records, unlogged: unlogged, progressCallback: (done, total) =>
            {
                double pct = (double)done / total * 100.0;
                double rate = done / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"Progress: {done:N0} / {total:N0} records ({pct:F1}%) at {rate:N0} rec/sec - Elapsed: {sw.Elapsed:mm\\:ss}");
            });

            sw.Stop();
            string manifestPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "eap_manifest.json");
            manifest.Save(manifestPath);

            Console.WriteLine("==================================================================");
            Console.WriteLine("              POSTGRESQL DATASET GENERATION COMPLETE              ");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"Total Rows          : {manifest.RowCount:N0}");
            Console.WriteLine($"Table Footprint     : {manifest.TableBytes / (1024 * 1024 * 1024.0):F2} GB");
            Console.WriteLine($"Index Footprint     : {manifest.IndexBytes / (1024 * 1024 * 1024.0):F2} GB");
            Console.WriteLine($"Elapsed Duration    : {sw.Elapsed}");
            Console.WriteLine($"Throughput          : {records / sw.Elapsed.TotalSeconds:N0} rows/sec");
            Console.WriteLine($"Manifest Path       : {manifestPath}");
            Console.WriteLine("==================================================================");
            return 0;
        }
        else if (sub == "verify")
        {
            string? countStr = GetArgValue(args, "--records");
            long records = countStr != null ? long.Parse(countStr) : 500_000_000L;

            Console.WriteLine($"Verifying {records:N0} records in PostgreSQL...");
            bool ok = await generator.VerifyAsync(records);
            Console.WriteLine($"Dataset Verification Status: {(ok ? "VALID [PASS]" : "FAILED")}");
            return ok ? 0 : 1;
        }

        return 1;
    }

    private static async Task<int> HandleEapAsync(string[] args)
    {
        if (args.Length < 2 || args[1].ToLowerInvariant() != "build") return 1;

        string? countStr = GetArgValue(args, "--records");
        long records = countStr != null ? long.Parse(countStr) : 500_000_000L;

        Console.WriteLine($"Building EAP Boolean Bitmap for {records:N0} accounts from PostgreSQL...");
        var bitmap = new EapBooleanBitmap(records);
        var duration = await bitmap.BuildFromPostgreSqlAsync(PostgreSqlPreflight.GetConnectionString(), progressCallback: (done, total) =>
        {
            Console.WriteLine($"Building EAP Bitmap: {done:N0} / {total:N0} accounts streamed...");
        });

        Console.WriteLine("==================================================================");
        Console.WriteLine("                  EAP BITMAP BUILD COMPLETE                       ");
        Console.WriteLine("==================================================================");
        Console.WriteLine($"Represented Records : {records:N0}");
        Console.WriteLine($"Raw Bitmap Memory   : {bitmap.RawMemoryBytes / (1024 * 1024.0):F2} MB ({bitmap.RawMemoryBytes / (1024.0 * 1024.0 * 1.048576):F2} MiB)");
        Console.WriteLine($"Bytes Per Record    : {(double)bitmap.RawMemoryBytes / records:F3} bytes (0.125 bytes/record)");
        Console.WriteLine($"Build Duration      : {duration}");
        Console.WriteLine($"Build Rate          : {records / duration.TotalSeconds:N0} accounts/sec");
        Console.WriteLine("==================================================================");
        return 0;
    }

    private static async Task<int> HandleBenchmarkAsync(string[] args)
    {
        if (args.Length < 2 || args[1].ToLowerInvariant() != "run") return 1;

        string suite = GetArgValue(args, "--suite") ?? "smoke";
        string? countStr = GetArgValue(args, "--records");
        long records = countStr != null ? long.Parse(countStr) : 500_000_000L;
        string connStr = PostgreSqlPreflight.GetConnectionString();

        Console.WriteLine($"Executing EAP Benchmark Suite '{suite}' (Dataset Scale: {records:N0})...");
        var results = new List<EapBenchmarkResult>();

        var candidates = new IEapBenchmarkCandidate[]
        {
            new Candidate1_PostgreSqlDirect(),
            new Candidate2_PostgreSqlCovering(),
            new Candidate3_BoundedResultCache(128 * 1024 * 1024),
            new Candidate4_WarrantV4(4_194_304),
            new Candidate5_EapBooleanBitmap(),
            new Candidate6_EapPackedCategory()
        };

        foreach (var c in candidates)
        {
            Console.WriteLine($"Initializing candidate: {c.Name}...");
            await c.InitializeAsync(records, connStr);

            if (suite is "smoke" or "eap-500m")
            {
                // Run Uniform Read
                var resUniform = await EapBenchmarkRunner.RunBenchmarkAsync(c, "Uniform-ReadOnly", records, totalRequests: 5_000, readPercent: 100.0, zipfian: false);
                results.Add(resUniform);

                // Run Skewed Zipfian
                var resZipf = await EapBenchmarkRunner.RunBenchmarkAsync(c, "Zipfian-85-15", records, totalRequests: 5_000, readPercent: 100.0, zipfian: true);
                results.Add(resZipf);
            }

            if (suite is "updates" or "eap-500m")
            {
                // Run 95% Read / 5% Write
                var res95 = await EapBenchmarkRunner.RunBenchmarkAsync(c, "Zipf-95Read-5Write", records, totalRequests: 2_000, readPercent: 95.0, zipfian: true);
                results.Add(res95);

                // Run 80% Read / 20% Write
                var res80 = await EapBenchmarkRunner.RunBenchmarkAsync(c, "Zipf-80Read-20Write", records, totalRequests: 2_000, readPercent: 80.0, zipfian: true);
                results.Add(res80);
            }

            await c.DisposeAsync();
        }

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "results", "eap-500m");
        EapBenchmarkRunner.SaveResults(results, outDir);

        PrintSummaryTable(results);
        return 0;
    }

    private static void PrintSummaryTable(List<EapBenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("| Candidate | Workload | Throughput (ops/s) | p50 (us) | p95 (us) | p99 (us) | Alloc/Op (B) | Resident RAM |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            Console.WriteLine($"| {r.CandidateName,-22} | {r.WorkloadName,-20} | {r.ThroughputOpsPerSec,14:N0} | {r.P50LatencyUs,8:F1} | {r.P95LatencyUs,8:F1} | {r.P99LatencyUs,8:F1} | {r.AllocBytesPerOp,10:F1} | {r.ResidentBytes / (1024 * 1024.0),8:F1} MB |");
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
