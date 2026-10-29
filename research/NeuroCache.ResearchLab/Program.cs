using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Benchmarks;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine("    NEUROCACHE RESEARCH LAB: 50M VALIDATION & PRODUCTION GATE SUITE      ");
        Console.WriteLine("==========================================================================");

        string currentDir = Directory.GetCurrentDirectory();
        string repoRoot = currentDir.EndsWith("NeuroCache.ResearchLab") && !currentDir.Contains("research")
            ? currentDir
            : (currentDir.Contains("research") ? Path.GetFullPath(Path.Combine(currentDir, "..", "..")) : currentDir);

        string resultsDir = Path.Combine(repoRoot, "research", "NeuroCache.ResearchLab", "Results", "2026-10-29_50M_Validation");
        Directory.CreateDirectory(resultsDir);

        bool runAll = args.Length == 0 || args.Contains("--all-validation");
        bool runValidate50M = runAll || args.Contains("--validate-50m");
        bool runClassGrowth = runAll || args.Contains("--class-growth");
        bool runConcurrency = runAll || args.Contains("--concurrency");
        bool runTournament50M = runAll || args.Contains("--tournament-50m");

        // Allow running the legacy 1M tournament if explicitly requested via --tournament-1m
        if (args.Contains("--tournament-1m"))
        {
            int accountCount = 1_000_000;
            int threads = 8;
            int iterations = 1_000_000;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--accounts" && i + 1 < args.Length && int.TryParse(args[i + 1], out int acc)) accountCount = acc;
                if (args[i] == "--threads" && i + 1 < args.Length && int.TryParse(args[i + 1], out int th)) threads = th;
                if (args[i] == "--iterations" && i + 1 < args.Length && int.TryParse(args[i + 1], out int iter)) iterations = iter;
            }

            Console.WriteLine($"Running legacy tournament with {accountCount:N0} accounts on {threads} worker threads...");
            var legacyResults = await TournamentBenchmarkRunner.RunTournamentAsync(accountCount, iterations, threads);
            return 0;
        }

        if (runValidate50M)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 2: EXHAUSTIVE 50M DATASET VALIDATION (POSTGRESQL COPY)");
            var report = Validation50MRunner.ExecuteValidation(resultsDir);
        }

        if (runClassGrowth)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 3: CLASS GROWTH & EXCEPTION DYNAMICS GATES");
            var growthResults = ClassGrowthValidationRunner.RunClassGrowthTests(resultsDir);
        }

        if (runConcurrency)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 6: CONCURRENCY, SEQLOCK & RECOVERY GATES");
            var concurrencyResults = SeqlockConcurrencyRunner.RunConcurrencyValidation(resultsDir);
        }

        if (runTournament50M)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 5: COMPLETE 50M CANDIDATE TOURNAMENT");
            var tournamentResults = await FairTournament50MRunner.RunTournament50MAsync(50_000_000, resultsDir);
        }

        Console.WriteLine();
        Console.WriteLine("==========================================================================");
        Console.WriteLine("   ALL VALIDATION SUITES COMPLETED SUCCESSFULLY                           ");
        Console.WriteLine($"   Results saved to: {resultsDir}");
        Console.WriteLine("==========================================================================");

        return 0;
    }
}
