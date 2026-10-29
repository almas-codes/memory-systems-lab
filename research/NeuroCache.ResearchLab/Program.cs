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
        Console.WriteLine("    NEUROCACHE RESEARCH LAB: FINAL RECONCILIATION & PRODUCTION GATE       ");
        Console.WriteLine("==========================================================================");

        string currentDir = Directory.GetCurrentDirectory();
        string repoRoot = currentDir.EndsWith("NeuroCache.ResearchLab") && !currentDir.Contains("research")
            ? currentDir
            : (currentDir.Contains("research") ? Path.GetFullPath(Path.Combine(currentDir, "..", "..")) : currentDir);

        string resultsDir = Path.Combine(repoRoot, "research", "NeuroCache.ResearchLab", "Results", "2026-10-29_Final_Reconciliation");
        Directory.CreateDirectory(resultsDir);

        bool runFullGate = args.Length == 0 || args.Contains("--full-final-gate");
        bool runExhaustiveParity = runFullGate || args.Contains("--exhaustive-parity") || args.Contains("--parity-50m") || args.Contains("--validate-50m");
        bool runConcurrencyGrowth = runFullGate || args.Contains("--concurrency-growth") || args.Contains("--concurrency-audit") || args.Contains("--concurrency") || args.Contains("--class-growth");
        bool runReconciliationTournament = runFullGate || args.Contains("--reconciliation-tournament") || args.Contains("--reconciliation-50m") || args.Contains("--tournament-50m");

        // Legacy / fallback triggers
        if (args.Contains("--tournament-1m"))
        {
            Console.WriteLine("Running legacy tournament with 1,000,000 accounts...");
            _ = await TournamentBenchmarkRunner.RunTournamentAsync(1_000_000, 1_000_000, 8);
            return 0;
        }

        if (runExhaustiveParity)
        {
            Console.WriteLine();
            Console.WriteLine(">>> STAGE 1: EXHAUSTIVE 50,000,000 ORACLE PARITY & POSTGRESQL SCAN");
            var parityReport = Exhaustive50MParityRunner.RunExhaustiveParity(resultsDir);
        }

        if (runConcurrencyGrowth)
        {
            Console.WriteLine();
            Console.WriteLine(">>> STAGE 2: 10 CLASS GROWTH SCENARIOS & SEQLOCK CONCURRENCY AUDIT");
            var concReport = ConcurrencyAndClassGrowthRunner.RunAudits(resultsDir);
        }

        if (runReconciliationTournament)
        {
            Console.WriteLine();
            Console.WriteLine(">>> STAGE 3: 50,000,000 RECORD BENCHMARK RECONCILIATION TOURNAMENT");
            var tournamentResults = await FinalReconciliationTournamentRunner.RunTournamentAsync(50_000_000, resultsDir);
        }

        Console.WriteLine();
        Console.WriteLine("==========================================================================");
        Console.WriteLine("   FINAL RECONCILIATION & PRODUCTION AUDIT SUITE COMPLETED                ");
        Console.WriteLine($"   Results Directory: {resultsDir}");
        Console.WriteLine("==========================================================================");

        return 0;
    }
}
