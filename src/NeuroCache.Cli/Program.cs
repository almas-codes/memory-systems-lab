using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NeuroCache.Benchmarks;
using NeuroCache.Core;
using NeuroCache.Storage;

namespace NeuroCache.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();

        try
        {
            switch (command)
            {
                case "inventory":
                    return HandleInventory();

                case "dataset":
                    return await HandleDatasetAsync(args);

                case "benchmark":
                    return await HandleBenchmarkAsync(args);

                case "compare":
                    return HandleCompare(args);

                case "report":
                    return HandleReport(args);

                default:
                    Console.WriteLine($"Unknown command: {command}");
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
NeuroCache.ResearchLab CLI
Usage:
  dotnet run --project src/NeuroCache.Cli -- inventory
  dotnet run --project src/NeuroCache.Cli -- dataset generate --records <count> [--shape small|medium|large|variable] [--path <dir>]
  dotnet run --project src/NeuroCache.Cli -- dataset verify [--path <dir>]
  dotnet run --project src/NeuroCache.Cli -- benchmark list
  dotnet run --project src/NeuroCache.Cli -- benchmark run --suite <smoke|micro|full|budget|ablation|adversarial> [--threads <N>] [--budget-mb <MB>]
  dotnet run --project src/NeuroCache.Cli -- compare --latest
  dotnet run --project src/NeuroCache.Cli -- report --format markdown
");
    }

    private static int HandleInventory()
    {
        var info = HostEnvironmentInfo.Capture();
        Console.WriteLine("==================================================================");
        Console.WriteLine("                  SYSTEM & HOST ENVIRONMENT                       ");
        Console.WriteLine("==================================================================");
        Console.WriteLine($"Operating System    : {info.OsDescription}");
        Console.WriteLine($"Architecture        : {info.Architecture}");
        Console.WriteLine($"Logical Cores       : {info.LogicalCores}");
        Console.WriteLine($".NET Runtime        : {info.FrameworkDescription} (Version: {info.DotNetVersion})");
        Console.WriteLine($"Server GC           : {info.IsServerGC}");
        Console.WriteLine($"Total RAM           : {info.TotalRamBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Free RAM            : {info.FreeRamBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Storage Drive       : {info.StorageDrive} ({info.StorageDriveType})");
        Console.WriteLine($"Total Storage       : {info.StorageTotalBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine($"Free Storage        : {info.StorageFreeBytes / (1024 * 1024 * 1024.0):F2} GB");
        Console.WriteLine("==================================================================");
        return 0;
    }

    private static async Task<int> HandleDatasetAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Subcommands: dataset generate, dataset verify");
            return 1;
        }

        string sub = args[1].ToLowerInvariant();
        string dataPath = GetArgValue(args, "--path") ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

        if (sub == "generate")
        {
            string? countStr = GetArgValue(args, "--records");
            long records = countStr != null ? long.Parse(countStr) : 100_000_000L;
            string shapeStr = GetArgValue(args, "--shape") ?? "small";
            RecordShape shape = Enum.Parse<RecordShape>(shapeStr, ignoreCase: true);
            int seed = int.Parse(GetArgValue(args, "--seed") ?? "42");

            Console.WriteLine($"Generating deterministic dataset: {records:N0} records, Shape: {shape}, Target: {dataPath}...");
            var generator = new DatasetGenerator();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var manifest = await generator.GenerateAsync(dataPath, records, shape, seed, (done, total) =>
            {
                double pct = (double)done / total * 100.0;
                double rate = done / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"Progress: {done:N0} / {total:N0} records ({pct:F1}%) at {rate:N0} rec/sec - Elapsed: {sw.Elapsed:mm\\:ss}");
            });

            sw.Stop();
            Console.WriteLine("==================================================================");
            Console.WriteLine("                   DATASET GENERATION COMPLETE                    ");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"Total Records       : {manifest.RecordCount:N0}");
            Console.WriteLine($"Record Shape        : {manifest.Shape}");
            Console.WriteLine($"Segments Created    : {manifest.SegmentCount}");
            Console.WriteLine($"Total Disk Footprint: {manifest.TotalBytesOnDisk / (1024 * 1024 * 1024.0):F2} GB");
            Console.WriteLine($"Elapsed Time        : {sw.Elapsed}");
            Console.WriteLine($"Generation Rate     : {records / sw.Elapsed.TotalSeconds:N0} records/sec");
            Console.WriteLine($"Manifest Saved      : {Path.Combine(dataPath, "manifest.json")}");
            Console.WriteLine("==================================================================");
            return 0;
        }
        else if (sub == "verify")
        {
            Console.WriteLine($"Verifying dataset at {dataPath}...");
            var report = await DatasetVerifier.VerifyAsync(dataPath);
            Console.WriteLine("==================================================================");
            Console.WriteLine("                   DATASET VERIFICATION REPORT                    ");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"Status              : {(report.IsValid ? "VALID [PASS]" : "FAILED")}");
            Console.WriteLine($"Declared Records    : {report.DeclaredRecordCount:N0}");
            Console.WriteLine($"Verified Index Rows : {report.VerifiedIndexEntries:N0}");
            Console.WriteLine($"Sample Checksums OK : {report.VerifiedSamplesCount:N0}");
            Console.WriteLine($"Corruptions Found   : {report.CorruptionsFound}");
            Console.WriteLine($"Verification Time   : {report.ElapsedTime}");
            Console.WriteLine($"Message             : {report.Message}");
            Console.WriteLine("==================================================================");
            return report.IsValid ? 0 : 1;
        }

        return 1;
    }

    private static async Task<int> HandleBenchmarkAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Subcommands: benchmark list, benchmark run");
            return 1;
        }

        string sub = args[1].ToLowerInvariant();
        if (sub == "list")
        {
            Console.WriteLine("Available Benchmark Suites:");
            Console.WriteLine("  smoke        : Fast sanity run of all 8 strategies on sample workload");
            Console.WriteLine("  micro        : Pure recompute vs SSD reads, single-read capsules vs extents");
            Console.WriteLine("  full         : Full matrix: Uniform, Zipf skew, Read/Write ratios (100, 99, 95, 80%), Working sets");
            Console.WriteLine("  budget       : Strict RAM budget constraints (128MB, 256MB, 512MB, 1024MB)");
            Console.WriteLine("  ablation     : NeuroCache Omega component ablation (witnesses, recomputation, capsules)");
            Console.WriteLine("  adversarial  : Adversarial stride, high write churn (50% writes), cache thrashing");
            Console.WriteLine();
            Console.WriteLine("Available Strategies:");
            Console.WriteLine("  DirectFileBaseline    : Direct unbuffered/buffered file random access");
            Console.WriteLine("  DotNetDictionary      : ConcurrentDictionary with strict RAM budget enforcement");
            Console.WriteLine("  DotNetMemoryCache     : Microsoft.Extensions.Caching.Memory with size limit");
            Console.WriteLine("  CertusEngramClaims    : Compact separated validity table (32B) + on-demand extent payload");
            Console.WriteLine("  SufficiencyFabric     : Compact certificates (36B) + pure computation seeds");
            Console.WriteLine("  NeuroCacheOmega       : State Witness Fabric + Answer Regions + Recomputation + Capsules");
            Console.WriteLine("  ProjectionCapsules    : Self-contained single-read immutable capsules");
            Console.WriteLine("  MemoryMappedPageCache : MemoryMappedFile OS page cache baseline");
            return 0;
        }
        else if (sub == "run")
        {
            string suite = GetArgValue(args, "--suite") ?? "smoke";
            string dataPath = GetArgValue(args, "--path") ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
            string resultsPath = GetArgValue(args, "--results") ?? Path.Combine(Directory.GetCurrentDirectory(), "results");
            int threads = int.Parse(GetArgValue(args, "--threads") ?? Environment.ProcessorCount.ToString());

            string manifestPath = Path.Combine(dataPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                Console.WriteLine($"Dataset manifest not found at {manifestPath}. Run dataset generate first.");
                return 1;
            }

            var manifest = RecordManifest.Load(manifestPath);
            Console.WriteLine($"Running benchmark suite '{suite}' against {manifest.RecordCount:N0} records (Concurrency: {threads})...");

            List<BenchmarkRunResult> results = suite.ToLowerInvariant() switch
            {
                "smoke" => await BenchmarkSuites.RunSmokeSuiteAsync(dataPath, manifest, resultsPath),
                "micro" => await BenchmarkSuites.RunMicroSuiteAsync(dataPath, manifest, resultsPath),
                "full" => await BenchmarkSuites.RunFullMatrixSuiteAsync(dataPath, manifest, resultsPath, concurrency: threads),
                "budget" => await BenchmarkSuites.RunBudgetSuiteAsync(dataPath, manifest, resultsPath),
                "ablation" => await BenchmarkSuites.RunAblationSuiteAsync(dataPath, manifest, resultsPath),
                "adversarial" => await BenchmarkSuites.RunAdversarialSuiteAsync(dataPath, manifest, resultsPath),
                _ => throw new ArgumentException($"Unknown suite: {suite}")
            };

            Console.WriteLine($"Benchmark suite '{suite}' completed with {results.Count} run results saved to {resultsPath}.");
            PrintSummaryTable(results);
            return 0;
        }

        return 1;
    }

    private static void PrintSummaryTable(List<BenchmarkRunResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("| Strategy | Workload | Throughput (ops/s) | p50 (us) | p95 (us) | p99 (us) | Hit Rate | Physical Reads | Alloc/Op (B) | Peak RAM (MB) |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            Console.WriteLine($"| {r.StrategyName,-20} | {r.WorkloadName,-20} | {r.Metrics.ThroughputOpsPerSec,16:N0} | {r.Metrics.P50LatencyUs,8:F1} | {r.Metrics.P95LatencyUs,8:F1} | {r.Metrics.P99LatencyUs,8:F1} | {r.Stats.HitRate * 100,7:F1}% | {r.Stats.PhysicalReads,14:N0} | {r.Metrics.AllocatedBytesPerOp,12:F1} | {r.Metrics.PeakWorkingSetBytes / (1024 * 1024),13} |");
        }
        Console.WriteLine();
    }

    private static int HandleCompare(string[] args)
    {
        string resultsPath = GetArgValue(args, "--results") ?? Path.Combine(Directory.GetCurrentDirectory(), "results");
        var jsonFiles = Directory.GetFiles(resultsPath, "*.json");
        if (jsonFiles.Length == 0)
        {
            Console.WriteLine("No benchmark results found to compare.");
            return 1;
        }

        Array.Sort(jsonFiles);
        string latestFile = jsonFiles[^1];
        Console.WriteLine($"Loading latest results from: {latestFile}");
        string json = File.ReadAllText(latestFile);
        var list = JsonSerializer.Deserialize<List<BenchmarkRunResult>>(json);
        if (list != null)
        {
            PrintSummaryTable(list);
        }
        return 0;
    }

    private static int HandleReport(string[] args)
    {
        string resultsPath = GetArgValue(args, "--results") ?? Path.Combine(Directory.GetCurrentDirectory(), "results");
        string docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs");
        Directory.CreateDirectory(docsPath);

        Console.WriteLine($"Generating final research report from results in {resultsPath}...");
        // Call report generator
        string reportFile = Path.Combine(docsPath, "FINAL-REPORT.md");
        ReportGenerator.GenerateMarkdownReport(resultsPath, reportFile);
        Console.WriteLine($"Report successfully generated at: {reportFile}");
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
