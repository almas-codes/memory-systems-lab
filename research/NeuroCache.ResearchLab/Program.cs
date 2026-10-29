using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Benchmarks;
using NeuroCache.ResearchLab.Data;
using NeuroCache.ResearchLab.Distributed;
using NeuroCache.ResearchLab.DynamicSchema;
using NeuroCache.ResearchLab.HybridEngine.Runners;
using NeuroCache.ResearchLab.Scale;
using NeuroCache.ResearchLab.Simd;

namespace NeuroCache.ResearchLab;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine("    NEUROCACHE RESEARCH LAB: PHASE 3 HYBRID TIERED ENGINE SUITE           ");
        Console.WriteLine("==========================================================================");

        string currentDir = Directory.GetCurrentDirectory();
        string repoRoot = currentDir.EndsWith("NeuroCache.ResearchLab") && !currentDir.Contains("research")
            ? currentDir
            : (currentDir.Contains("research") ? Path.GetFullPath(Path.Combine(currentDir, "..", "..")) : currentDir);

        string hybridResultsDir = Path.Combine(repoRoot, "research", "NeuroCache.ResearchLab", "Results", "2026-10-30_Hybrid_Tiered_Engine");
        Directory.CreateDirectory(hybridResultsDir);

        string phase2ResultsDir = Path.Combine(repoRoot, "research", "NeuroCache.ResearchLab", "Results", "2026-10-29_Phase2_Advanced_Research");
        Directory.CreateDirectory(phase2ResultsDir);

        string phase1ResultsDir = Path.Combine(repoRoot, "research", "NeuroCache.ResearchLab", "Results", "2026-10-29_Final_Reconciliation");
        Directory.CreateDirectory(phase1ResultsDir);

        bool runHybridAll = args.Contains("--hybrid-all");
        bool runHybridReconcile = runHybridAll || args.Contains("--hybrid-reconcile") || args.Contains("--reconcile-hybrid");
        bool runHybridIntegration = runHybridAll || args.Contains("--hybrid-integration") || args.Contains("--integration");
        bool runHybridBenchmark = runHybridAll || args.Contains("--hybrid-benchmark") || args.Contains("--bench-hybrid");

        bool runPhase2All = args.Contains("--phase2-all");
        bool runTrack1 = runPhase2All || args.Contains("--track1-repartition") || args.Contains("--repartition");
        bool runTrack2 = runPhase2All || args.Contains("--track2-simd") || args.Contains("--simd");
        bool runTrack3 = runPhase2All || args.Contains("--track3-scale500m") || args.Contains("--scale-500m");
        bool runTrack4 = runPhase2All || args.Contains("--track4-replication") || args.Contains("--replication");

        // Phase 1 triggers
        bool runFullGate = args.Contains("--full-final-gate");
        bool runExhaustiveParity = runFullGate || args.Contains("--exhaustive-parity") || args.Contains("--parity-50m") || args.Contains("--validate-50m");
        bool runConcurrencyGrowth = runFullGate || args.Contains("--concurrency-growth") || args.Contains("--concurrency-audit") || args.Contains("--concurrency") || args.Contains("--class-growth");
        bool runReconciliationTournament = runFullGate || args.Contains("--reconciliation-tournament") || args.Contains("--reconciliation-50m") || args.Contains("--tournament-50m");

        // If no explicit arguments provided, default to running the full Hybrid Tiered Engine suite
        if (args.Length == 0)
        {
            runHybridAll = true;
            runHybridReconcile = true;
            runHybridIntegration = true;
            runHybridBenchmark = true;
        }

        // Hybrid Tiered Engine Suite
        if (runHybridReconcile)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 3: HYBRID EVIDENCE RECONCILIATION & BENCHMARK AUDIT");
            var reconcileReport = await HybridReconciliationRunner.RunReconciliationAsync(hybridResultsDir);
        }

        if (runHybridIntegration)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 3: 18 HYBRID INTEGRATION TESTS & CRASH RECOVERY GATES");
            var intReport = await HybridIntegrationRunner.RunIntegrationTestsAsync(hybridResultsDir);
        }

        if (runHybridBenchmark)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 3: FULL HYBRID SYSTEM BENCHMARK TOURNAMENT (10M / 50M)");
            var benchReport = await HybridBenchmarkRunner.RunBenchmarkSuiteAsync(10_000_000, hybridResultsDir);
        }

        if (runExhaustiveParity)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 1: EXHAUSTIVE 50,000,000 ORACLE PARITY & POSTGRESQL SCAN");
            var parityReport = Exhaustive50MParityRunner.RunExhaustiveParity(phase1ResultsDir);
        }

        if (runConcurrencyGrowth)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 1: 10 CLASS GROWTH SCENARIOS & SEQLOCK CONCURRENCY AUDIT");
            var concReport = ConcurrencyAndClassGrowthRunner.RunAudits(phase1ResultsDir);
        }

        if (runReconciliationTournament)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 1: 50,000,000 RECORD BENCHMARK RECONCILIATION TOURNAMENT");
            var tournamentResults = await FinalReconciliationTournamentRunner.RunTournamentAsync(50_000_000, phase1ResultsDir);
        }

        // Phase 2 Advanced Tracks
        if (runTrack1)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 2 - TRACK 1: ONLINE EQUIVALENCE-CLASS REPARTITIONING (SCHEMA-ONLINE-REPARTITION-001)");
            var track1Report = SchemaOnlineRepartitionRunner.RunAllTestsAndBenchmarks(phase2ResultsDir);
        }

        if (runTrack2)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 2 - TRACK 2: SIMD ACCELERATION FOR NUMERIC & BULK QUERIES (SIMD-BITSLICE-001)");
            var track2Report = await SimdBitSlicedThresholdRunner.RunExperimentAsync(10_000_000, phase2ResultsDir);
        }

        if (runTrack3)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 2 - TRACK 3: 500-MILLION-RECORD SCALING ENVELOPE (SCALE-500M-001)");
            var track3Report = Scale500MRunner.RunScalingEnvelope(phase2ResultsDir);
        }

        if (runTrack4)
        {
            Console.WriteLine();
            Console.WriteLine(">>> PHASE 2 - TRACK 4: MULTI-NODE CDC REPLICATION & LOCAL SEQLOCKS (REPL-CDC-001)");
            var track4Report = DistributedCdcReplicationRunner.RunReplicationTournament(phase2ResultsDir);
        }

        Console.WriteLine();
        Console.WriteLine("==========================================================================");
        Console.WriteLine("   NEUROCACHE RESEARCH LAB SUITE COMPLETED SUCCESSFULLY                  ");
        Console.WriteLine($"   Hybrid Results Directory: {hybridResultsDir}");
        Console.WriteLine("==========================================================================");

        return 0;
    }
}
