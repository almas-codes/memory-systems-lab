using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Benchmarks;

namespace NeuroCache.ResearchLab;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine("    NEUROCACHE RESEARCH LAB: TOURNAMENT & VERIFICATION SYSTEM            ");
        Console.WriteLine("==========================================================================");

        int accountCount = 1_000_000;
        int threads = 8;
        int iterations = 1_000_000;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--accounts" && i + 1 < args.Length && int.TryParse(args[i + 1], out int acc))
            {
                accountCount = acc;
            }
            else if (args[i] == "--threads" && i + 1 < args.Length && int.TryParse(args[i + 1], out int th))
            {
                threads = th;
            }
            else if (args[i] == "--iterations" && i + 1 < args.Length && int.TryParse(args[i + 1], out int iter))
            {
                iterations = iter;
            }
        }

        Console.WriteLine($"Running tournament with {accountCount:N0} accounts on {threads} worker threads...");
        var results = await TournamentBenchmarkRunner.RunTournamentAsync(accountCount, iterations, threads);

        string currentDir = Directory.GetCurrentDirectory();
        // Check if we are running in the repo root or inside research/NeuroCache.ResearchLab
        string targetDir = currentDir.EndsWith("NeuroCache.ResearchLab") && !currentDir.Contains("research")
            ? Path.Combine(currentDir, "research", "NeuroCache.ResearchLab")
            : currentDir;

        string rawDir = Path.Combine(targetDir, "Results", "2026-10-29", "Raw");
        string sumDir = Path.Combine(targetDir, "Results", "2026-10-29", "Summaries");

        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(sumDir);

        string jsonPath = Path.Combine(rawDir, $"tournament-{accountCount / 1000}k.json");
        string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
        Console.WriteLine($"Wrote raw results to: {jsonPath}");

        string mdPath = Path.Combine(sumDir, $"tournament-summary-{accountCount / 1000}k.md");
        using (var sw = new StreamWriter(mdPath))
        {
            sw.WriteLine($"# Candidate Tournament Benchmark Results ({accountCount:N0} Accounts)");
            sw.WriteLine();
            sw.WriteLine($"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ");
            sw.WriteLine($"**Threads:** {threads} | **Iterations:** {iterations:N0} per thread  ");
            sw.WriteLine();
            sw.WriteLine("| Candidate ID | Candidate Name | Family | Memory (MB) | Memory (MiB) | Bits/Acc | ST Read (Mops/s) | MT Read (Mops/s) | Zipf Read (Mops/s) | MT Write (Mops/s) |");
            sw.WriteLine("|---|---|---|---|---|---|---|---|---|---|");

            foreach (var r in results.OrderBy(r => r.BitsPerAccount))
            {
                sw.WriteLine($"| `{r.CandidateId}` | {r.Name} | {r.Family} | {r.DecimalMb:F2} | {r.BinaryMib:F2} | {r.BitsPerAccount:F2} | {r.SingleThreadReadMops:F2} | {r.MultiThreadReadMops:F2} | {r.ZipfianReadMops:F2} | {r.MultiThreadWriteMops:F2} |");
            }
        }
        Console.WriteLine($"Wrote summary report to: {mdPath}");

        return 0;
    }
}
