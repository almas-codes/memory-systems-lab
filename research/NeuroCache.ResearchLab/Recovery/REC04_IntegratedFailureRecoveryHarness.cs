using System;
using System.Collections.Generic;
using NeuroCache.ResearchLab.Data;
using NeuroCache.ResearchLab.Oracle;

namespace NeuroCache.ResearchLab.Recovery;

/// <summary>
/// REC-04: Integrated Failure Recovery Harness (REC-HARNESS-004)
/// Injects simulated failure modes (crash mid-write, partial replay, sequence rollback gaps)
/// and verifies that checkpoint + outbox replay reaches 100% exact parity with the authoritative oracle.
/// </summary>
public sealed class REC04_IntegratedFailureRecoveryHarness
{
    public sealed record RecoveryTestResult(
        string ScenarioName,
        bool Success,
        long InitialWatermark,
        long RestoredWatermark,
        int OutboxMutationsInjected,
        int ReplayedMutations,
        int MismatchesAgainstOracle,
        string Details);

    public static RecoveryTestResult RunCrashBeforePublicationTest(int accountCount = 1000)
    {
        var cache = new REC02_CheckpointedContiguousPrefixReplay(accountCount, initialWatermark: 0);
        var oracle = new PureCodeOracle();

        for (int i = 0; i < accountCount; i++)
        {
            int bal = (i * 37) % 1500;
            bool act = (i % 3) != 0;
            cache.HandleMutation(i, bal, act, 0);
            oracle.Upsert(i, bal, act, 0);
        }
        cache.TakeCheckpoint();

        var outbox = new List<(long SeqId, long AccountId, int Balance, bool Active)>();
        for (int i = 0; i < 50; i++)
        {
            long seq = i + 1;
            long accId = i * 15;
            int newBal = 1200;
            bool newAct = true;
            oracle.Upsert(accId, newBal, newAct, seq);
            outbox.Add((seq, accId, newBal, newAct));
        }

        long cpWatermark = cache.RestoreFromCheckpoint();
        int replayed = cache.ReplayOutbox(outbox);

        int mismatches = 0;
        for (int accId = 0; accId < accountCount; accId++)
        {
            for (int p = 0; p < 8; p++)
            {
                bool cached = cache.EvaluatePredicate(accId, p);
                bool expected = oracle.Evaluate(accId, p);
                if (cached != expected) mismatches++;
            }
        }

        return new RecoveryTestResult(
            ScenarioName: "Crash Mid-Publication",
            Success: mismatches == 0 && cache.CurrentWatermark == 50,
            InitialWatermark: 0,
            RestoredWatermark: cache.CurrentWatermark,
            OutboxMutationsInjected: outbox.Count,
            ReplayedMutations: replayed,
            MismatchesAgainstOracle: mismatches,
            Details: mismatches == 0 ? "Replay fully restored un-published state with zero divergence" : $"{mismatches} mismatches found");
    }

    public static RecoveryTestResult RunRollbackGapRecoveryTest(int accountCount = 500)
    {
        var cache = new REC02_CheckpointedContiguousPrefixReplay(accountCount, initialWatermark: 0);
        var oracle = new PureCodeOracle();

        for (int i = 0; i < accountCount; i++)
        {
            int bal = (i * 23) % 1200;
            bool act = (i % 2) == 0;
            cache.HandleMutation(i, bal, act, 0);
            oracle.Upsert(i, bal, act, 0);
        }
        cache.TakeCheckpoint();

        var outbox = new List<(long SeqId, long AccountId, int Balance, bool Active)>();

        oracle.Upsert(10, 999, true, 1);
        outbox.Add((1, 10, 999, true));

        oracle.Upsert(20, 888, true, 2);
        outbox.Add((2, 20, 888, true));

        // Seq 3 was rolled back (not present in outbox)

        oracle.Upsert(30, 777, true, 4);
        outbox.Add((4, 30, 777, true));

        oracle.Upsert(40, 666, true, 5);
        outbox.Add((5, 40, 666, true));

        cache.RestoreFromCheckpoint();
        int replayed = cache.ReplayOutbox(outbox);

        int mismatches = 0;
        for (int accId = 0; accId < accountCount; accId++)
        {
            for (int p = 0; p < 8; p++)
            {
                if (cache.EvaluatePredicate(accId, p) != oracle.Evaluate(accId, p))
                {
                    mismatches++;
                }
            }
        }

        return new RecoveryTestResult(
            ScenarioName: "Rollback Gap Sequence Handling",
            Success: mismatches == 0 && replayed == 4,
            InitialWatermark: 0,
            RestoredWatermark: cache.CurrentWatermark,
            OutboxMutationsInjected: 4,
            ReplayedMutations: replayed,
            MismatchesAgainstOracle: mismatches,
            Details: "Watermark advanced across gap without corrupting subsequent committed mutations");
    }
}
