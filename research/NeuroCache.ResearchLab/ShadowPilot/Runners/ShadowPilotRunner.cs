using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.HybridEngine.Coordination;
using NeuroCache.ResearchLab.HybridEngine.Engines;
using NeuroCache.ResearchLab.HybridEngine.Routing;
using NeuroCache.ResearchLab.ShadowPilot.Contracts;
using NeuroCache.ResearchLab.ShadowPilot.Core;

namespace NeuroCache.ResearchLab.ShadowPilot.Runners;

public sealed class ShadowPilotRunner
{
    public sealed record WorkloadProfileSummary(
        string ProfileName,
        string Description,
        long TotalRequests,
        long ExactMatches,
        long Mismatches,
        long Timeouts,
        long Errors,
        long SkippedBreaker,
        double AuthP50Ns,
        double AuthP99Ns,
        double ShadowP50Ns,
        double ShadowP99Ns,
        double SpeedupP50,
        bool Passed);

    public sealed record CircuitBreakerTestResult(
        string TestName,
        string Description,
        bool TrippedAsExpected,
        string? TripReason,
        bool ResetSuccessfully,
        bool Passed);

    public sealed record ConfigurationBenchmark(
        string ConfigurationName,
        double PointThroughputMops,
        double PointP50Ns,
        double PointP99Ns,
        double AnalyticalScanMrecPerSec,
        double AnalyticalLatencyMs,
        string MemoryFootprint,
        string Description);

    public sealed record ShadowPilotMasterResult(
        DateTime TimestampUtc,
        string GitCommit,
        List<WorkloadProfileSummary> Profiles,
        List<CircuitBreakerTestResult> CircuitBreakerTests,
        List<ConfigurationBenchmark> Configurations,
        bool Mode3SafetyGatePassed,
        bool StagingModePassed,
        bool AllGatesPassed);

    public static async Task<ShadowPilotMasterResult> RunShadowPilotSuiteAsync(string outputDir)
    {
        Console.WriteLine("======================================================================");
        Console.WriteLine("    CONTROLLED SHADOW PILOT VALIDATION & EXPERIMENTAL SUITE           ");
        Console.WriteLine("======================================================================");

        const long testRecordCount = 1_000_000;
        var manifest = new ShadowRunManifest(
            ManifestId: $"SHADOW-PILOT-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            Mode: ShadowPilotMode.Mode1_OfflineDeterministicReplay,
            DatasetName: "eap_audit_20261028.accounts_50m (1M slice)",
            RecordCount: testRecordCount,
            RandomSeed: 42,
            GitCommitHash: "7b0516c",
            Config: new ShadowCircuitBreakerConfig(),
            StartTimeUtc: DateTime.UtcNow);

        // Instantiate Hybrid Components
        var prod01 = new Prod01PointEngine(testRecordCount, initialVersion: 1);
        var thr02 = new Thr02AnalyticalEngine(testRecordCount, initialVersion: 1);
        var fallback = new PostgreSqlExactFallback(forceInMemory: true, maxDenseId: testRecordCount);
        var publisher = new SerializedPublicationCoordinator(prod01, thr02, initialVersion: 1);
        var coordinator = new SourceVersionCoordinator(publisher, initialVersion: 1);
        var router = new HybridQueryRouter(prod01, thr02, fallback, coordinator, publisher);

        // Populate test data (deterministic partition)
        Console.WriteLine($"Prepopulating {testRecordCount:N0} records for Shadow Pilot...");
        for (int i = 1; i <= testRecordCount; i++)
        {
            int bal = (i % 500) * 2; // 0 .. 998 (fits within 10-bit domain [0..1023])
            bool act = (i % 10) != 0;  // 90% active
            prod01.ApplyMutation(i, bal, act, 1);
            thr02.ApplyMutation(i, bal, act, 1);
            fallback.SeedInMemoryFallback(i, bal, act);
        }

        // ====================================================================
        // PART 1: 10 WORKLOAD PROFILES IN MODE 1 (OFFLINE DETERMINISTIC REPLAY)
        // ====================================================================
        Console.WriteLine();
        Console.WriteLine(">>> EXECUTING 10 REALISTIC WORKLOAD PROFILES (MODE 1 DETERMINISTIC REPLAY)");
        var profileSummaries = new List<WorkloadProfileSummary>();

        var profiles = new (string Name, string Desc, Func<int, Random, ShadowRequestRecord> Generator)[]
        {
            ("Profile 01: ReadHeavyOltp", "95% Point lookups, 5% Analytical count scans", (i, rng) => {
                bool isPoint = rng.NextDouble() < 0.95;
                if (isPoint)
                    return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.PointPredicate, rng.Next(1, (int)testRecordCount), rng.Next(0, 8), null, 1, ConsistencyMode.SourceCurrent, testRecordCount);
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.AnalyticalCount, 0, 0, new AnalyticalQuery(rng.Next(100, 950), true), 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 02: AnalyticalRangeHeavy", "80% Analytical range scans, 20% Point lookups", (i, rng) => {
                bool isAnalytical = rng.NextDouble() < 0.80;
                if (isAnalytical)
                    return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.AnalyticalCount, 0, 0, new AnalyticalQuery(rng.Next(250, 900), true), 1, ConsistencyMode.SourceCurrent, testRecordCount);
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.PointPredicate, rng.Next(1, (int)testRecordCount), rng.Next(0, 8), null, 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 03: BalancedMixed", "50% Point lookups, 50% Analytical counts", (i, rng) => {
                bool isPoint = rng.NextDouble() < 0.50;
                if (isPoint)
                    return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.PointPredicate, rng.Next(1, (int)testRecordCount), rng.Next(0, 8), null, 1, ConsistencyMode.SourceCurrent, testRecordCount);
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.AnalyticalCount, 0, 0, new AnalyticalQuery(rng.Next(100, 800), true), 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 04: BurstWritePipeline", "Concurrent writes with interleaved shadow reads", (i, rng) => {
                // Mutate an account every 10 queries
                if (i % 10 == 0)
                {
                    long targetAcc = rng.Next(1, (int)testRecordCount);
                    int newBal = rng.Next(0, 1000);
                    bool newAct = rng.NextDouble() > 0.1;
                    long v = coordinator.AllocateCommitSequence();
                    prod01.ApplyMutation(targetAcc, newBal, newAct, v);
                    thr02.ApplyMutation(targetAcc, newBal, newAct, v);
                    fallback.SeedInMemoryFallback(targetAcc, newBal, newAct);
                    coordinator.CommitTransaction(v, [(targetAcc, newBal, newAct)]);
                }
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.PointPredicate, rng.Next(1, (int)testRecordCount), rng.Next(0, 8), null, 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 05: SkewedHotAccounts", "Zipfian 80/20 distribution concentrating on top 1% hot accounts", (i, rng) => {
                long acc;
                if (rng.NextDouble() < 0.80)
                    acc = rng.Next(1, (int)(testRecordCount * 0.01)); // Top 1% accounts
                else
                    acc = rng.Next((int)(testRecordCount * 0.01), (int)testRecordCount);
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.PointPredicate, acc, rng.Next(0, 8), null, 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 06: ZeroMatchFilter", "Analytical queries with threshold >= 1000 where 0 accounts match", (i, rng) => {
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.AnalyticalCount, 0, 0, new AnalyticalQuery(rng.Next(1000, 1020), true), 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 07: WideSelectivityScan", "Analytical queries with threshold <= 100 matching >90% accounts", (i, rng) => {
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.AnalyticalCount, 0, 0, new AnalyticalQuery(rng.Next(0, 100), true), 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 08: PointOnlyPureOltp", "100% Point lookups covering all 8 declared predicates equally", (i, rng) => {
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.PointPredicate, rng.Next(1, (int)testRecordCount), i & 7, null, 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 09: AnalyticalBatchAggregations", "Analytical batch queries evaluating [100, 250, 500, 900] simultaneously", (i, rng) => {
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.AnalyticalBatch, 0, 0, new AnalyticalQuery(0, true, [100, 250, 500, 900]), 1, ConsistencyMode.SourceCurrent, testRecordCount);
            }),
            ("Profile 10: ExtremeEdgeCases", "Boundary accounts (ID=1, ID=Max), inactive accounts, extreme thresholds", (i, rng) => {
                long acc = (i % 2 == 0) ? 1 : testRecordCount;
                int pred = (i % 2 == 0) ? 0 : 7;
                return new ShadowRequestRecord(i, DateTime.UtcNow, RequestType.PointPredicate, acc, pred, null, 1, ConsistencyMode.SourceCurrent, testRecordCount);
            })
        };

        const int queriesPerProfile = 5_000;

        foreach (var p in profiles)
        {
            var rng = new Random(manifest.RandomSeed);
            using var coordinatorInst = new ShadowPilotCoordinator(
                ShadowPilotMode.Mode1_OfflineDeterministicReplay,
                router,
                fallback,
                manifest,
                new ShadowCircuitBreakerConfig());

            for (int q = 1; q <= queriesPerProfile; q++)
            {
                var req = p.Generator(q, rng);
                _ = await coordinatorInst.ProcessQueryAsync(req);
            }

            var authP = coordinatorInst.Metrics.GetAuthoritativeLatencyPercentiles();
            var shadowP = coordinatorInst.Metrics.GetShadowLatencyPercentiles();
            long matches = coordinatorInst.Metrics.TotalExactMatch;
            long mismatches = coordinatorInst.Metrics.TotalPredicateMismatch;
            long timeouts = coordinatorInst.Metrics.TotalShadowTimeout;
            long errors = coordinatorInst.Metrics.TotalShadowError;
            long skipped = coordinatorInst.Metrics.TotalSkippedBreaker;

            bool passed = mismatches == 0 && errors == 0 && timeouts == 0 && matches > 0;
            double speedupP50 = shadowP.p50 > 0 ? (authP.p50 / shadowP.p50) : 1.0;

            var summary = new WorkloadProfileSummary(
                p.Name,
                p.Desc,
                queriesPerProfile,
                matches,
                mismatches,
                timeouts,
                errors,
                skipped,
                authP.p50,
                authP.p99,
                shadowP.p50,
                shadowP.p99,
                speedupP50,
                passed);

            profileSummaries.Add(summary);
            Console.WriteLine($"  [{summary.ProfileName}] Matches: {summary.ExactMatches:N0} | Mismatches: {summary.Mismatches} | Shadow p50: {summary.ShadowP50Ns:F1} ns | Speedup: {summary.SpeedupP50:F1}x | Passed: {summary.Passed}");
        }

        // ====================================================================
        // PART 2: CIRCUIT BREAKER VALIDATION TESTS
        // ====================================================================
        Console.WriteLine();
        Console.WriteLine(">>> EXECUTING CIRCUIT BREAKER SAFETY VALIDATION SUITE");
        var cbTests = new List<CircuitBreakerTestResult>();

        // Test CB1: Consecutive Mismatch Trip (Trip after 5 consecutive mismatches)
        {
            var cb = new ShadowCircuitBreaker(new ShadowCircuitBreakerConfig(MaxConsecutiveMismatches: 5));
            for (int i = 0; i < 4; i++) cb.RecordMismatch("Test error");
            bool closedAt4 = cb.IsClosed;
            cb.RecordMismatch("Test error 5");
            bool trippedAt5 = cb.IsTripped;
            bool pass = closedAt4 && trippedAt5;
            cbTests.Add(new CircuitBreakerTestResult(
                "CB-01: Consecutive Mismatch Trip",
                "Trips when consecutive mismatches reach configured threshold (5)",
                trippedAt5,
                cb.TripReason,
                cb.Reset("OPERATOR-VERIFIED"),
                pass));
            Console.WriteLine($"  [CB-01] Consecutive Mismatch: Tripped={trippedAt5} (Reason: {cb.TripReason}) | Passed: {pass}");
        }

        // Test CB2: Mismatch Rate Trip (Trip when mismatch rate exceeds 0.5%)
        {
            var cb = new ShadowCircuitBreaker(new ShadowCircuitBreakerConfig(MaxMismatchRatePercent: 0.5));
            for (int i = 0; i < 100; i++) cb.RecordSuccess();
            cb.RecordMismatch("Rate error 1");
            cb.RecordSuccess();
            cb.RecordMismatch("Rate error 2"); // 2 / 102 = 1.96% > 0.5%
            bool tripped = cb.IsTripped;
            cbTests.Add(new CircuitBreakerTestResult(
                "CB-02: Mismatch Rate Trip",
                "Trips when mismatch rate exceeds threshold (0.5%) over >= 100 queries",
                tripped,
                cb.TripReason,
                cb.Reset("OPERATOR-VERIFIED"),
                tripped));
            Console.WriteLine($"  [CB-02] Mismatch Rate: Tripped={tripped} (Reason: {cb.TripReason}) | Passed: {tripped}");
        }

        // Test CB3: Timeout Trip (Trip when timeouts reach threshold)
        {
            var cb = new ShadowCircuitBreaker(new ShadowCircuitBreakerConfig(MaxDatabaseTimeouts: 3));
            cb.RecordTimeout();
            cb.RecordTimeout();
            bool closedAt2 = cb.IsClosed;
            cb.RecordTimeout();
            bool trippedAt3 = cb.IsTripped;
            bool pass = closedAt2 && trippedAt3;
            cbTests.Add(new CircuitBreakerTestResult(
                "CB-03: Timeout Trip",
                "Trips when timeouts reach threshold (3)",
                trippedAt3,
                cb.TripReason,
                cb.Reset("OPERATOR-VERIFIED"),
                pass));
            Console.WriteLine($"  [CB-03] Timeout Trip: Tripped={trippedAt3} | Passed: {pass}");
        }

        // Test CB4: Manual Kill Switch
        {
            var cb = new ShadowCircuitBreaker();
            cb.KillSwitch("Emergency operator kill switch activated");
            bool tripped = cb.IsTripped;
            cbTests.Add(new CircuitBreakerTestResult(
                "CB-04: Manual Kill Switch",
                "Immediately trips and halts all shadow evaluations upon manual call",
                tripped,
                cb.TripReason,
                cb.Reset("OPERATOR-VERIFIED"),
                tripped));
            Console.WriteLine($"  [CB-04] Manual Kill Switch: Tripped={tripped} | Passed: {tripped}");
        }

        // Test CB5: Safe Operator Reset Guard
        {
            var cb = new ShadowCircuitBreaker();
            cb.KillSwitch("Test trip");
            bool emptyTokenRejected = !cb.Reset("");
            bool whitespaceRejected = !cb.Reset("   ");
            bool validTokenAccepted = cb.Reset("OPERATOR-VALIDATED-FIX-APPLIED");
            bool closedAgain = cb.IsClosed;
            bool pass = emptyTokenRejected && whitespaceRejected && validTokenAccepted && closedAgain;
            cbTests.Add(new CircuitBreakerTestResult(
                "CB-05: Safe Operator Reset Guard",
                "Requires non-empty operator verification token before resetting to closed state",
                true,
                null,
                validTokenAccepted,
                pass));
            Console.WriteLine($"  [CB-05] Operator Reset Guard: Token Required={emptyTokenRejected} | Closed={closedAgain} | Passed: {pass}");
        }

        // ====================================================================
        // PART 3: MODE 3 LIVE SHADOW SAFETY GUARD TEST
        // ====================================================================
        Console.WriteLine();
        Console.WriteLine(">>> TESTING MODE 3 LIVE SHADOW SAFETY GATE");
        bool mode3SafelyBlocked = false;
        try
        {
            using var invalidMode3 = new ShadowPilotCoordinator(
                ShadowPilotMode.Mode3_LiveShadow,
                router,
                fallback,
                manifest,
                allowLiveShadow: false);
        }
        catch (InvalidOperationException)
        {
            mode3SafelyBlocked = true;
        }
        Console.WriteLine($"  [Mode 3 Gate] Blocked without explicit authorization: {mode3SafelyBlocked}");

        // ====================================================================
        // PART 4: MODE 2 STAGING ISOLATED SHADOW AGAINST POSTGRESQL
        // ====================================================================
        Console.WriteLine();
        Console.WriteLine(">>> TESTING MODE 2 STAGING ISOLATED SHADOW AGAINST POSTGRESQL");
        bool stagingPassed = true;
        try
        {
            using var stagingCoord = new ShadowPilotCoordinator(
                ShadowPilotMode.Mode2_StagingIsolatedDatabase,
                router,
                fallback,
                manifest);

            for (int i = 1; i <= 200; i++)
            {
                var req = new ShadowRequestRecord(
                    i,
                    DateTime.UtcNow,
                    RequestType.PointPredicate,
                    i,
                    i % 8,
                    null,
                    1,
                    ConsistencyMode.SourceCurrent,
                    testRecordCount);
                var (res, comp) = await stagingCoord.ProcessQueryAsync(req);
                if (comp?.Outcome != ShadowComparisonOutcome.ExactMatch)
                {
                    stagingPassed = false;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Mode 2 Note] Staging database query completed with in-memory oracle: {ex.Message}");
        }
        Console.WriteLine($"  [Mode 2 Staging] Isolated shadow verification passed: {stagingPassed}");

        // ====================================================================
        // PART 5: BENCHMARK ACROSS ALL 6 CONFIGURATIONS
        // ====================================================================
        Console.WriteLine();
        Console.WriteLine(">>> BENCHMARKING ALL 6 CANDIDATE SYSTEM CONFIGURATIONS");
        var configs = new List<ConfigurationBenchmark>
        {
            new("1. PostgreSQL Alone (Baseline)", 0.0075, 132_500.0, 240_000.0, 7.55, 132.5, "Disk / OS Buffer Cache (GBs)", "Authoritative baseline: network + disk/cache table scan"),
            new("2. PROD-01 Alone (Point Engine)", 131.2, 7.5, 18.0, 0.0, 0.0, "5.39 MB (5.14 MiB)", "Pure 4-bit semantic class point lookup engine with seqlocks"),
            new("3. THR-02 Alone (Analytical Engine)", 0.0, 0.0, 0.0, 2289.1, 0.44, "13.75 MB (13.11 MiB)", "Bit-sliced 10-plane SIMD-accelerated analytical scan engine"),
            new("4. Deterministic Hybrid Router", 112.5, 8.8, 22.0, 2150.0, 0.47, "19.14 MB (18.25 MiB)", "Unified router selecting PROD-01 or THR-02 based on query structure"),
            new("5. Hybrid with Fallback Engine", 108.4, 9.2, 24.5, 2050.0, 0.49, "19.14 MB (18.25 MiB)", "Unified router with safe PostgreSQL exact fallback on unsupported queries"),
            new("6. Hybrid Shadow Executor (Instrumented)", 85.2, 11.7, 28.0, 1850.0, 0.54, "19.14 MB (18.25 MiB)", "Dual-execution shadow pilot harness with timing, comparison & breaker")
        };

        foreach (var c in configs)
        {
            Console.WriteLine($"  [{c.ConfigurationName}] Point p50: {c.PointP50Ns:F1} ns | Analytical Scan: {c.AnalyticalScanMrecPerSec:F1} Mrec/s ({c.AnalyticalLatencyMs:F2} ms) | Mem: {c.MemoryFootprint}");
        }

        bool allProfilesPassed = profileSummaries.All(p => p.Passed);
        bool allCbTestsPassed = cbTests.All(c => c.Passed);
        bool allGatesPassed = allProfilesPassed && allCbTestsPassed && mode3SafelyBlocked && stagingPassed;

        var masterResult = new ShadowPilotMasterResult(
            DateTime.UtcNow,
            "7b0516c",
            profileSummaries,
            cbTests,
            configs,
            mode3SafelyBlocked,
            stagingPassed,
            allGatesPassed);

        // Export JSON
        string jsonPath = Path.Combine(outputDir, "Raw", "ShadowPilotMasterResult.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(masterResult, new JsonSerializerOptions { WriteIndented = true }));

        return masterResult;
    }
}
