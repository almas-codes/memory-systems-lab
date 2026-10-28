using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NeuroCache.EapResearch;
using Xunit;

namespace NeuroCache.EapResearch.Tests;

public class EapRollbackGapAndRecoveryTests
{
    private readonly string _connectionString = PostgreSqlPreflight.GetConnectionString();

    private async Task SetupTestTablesAsync(string accountsTable, string outboxTable)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand($@"
            CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
            DROP TABLE IF EXISTS {accountsTable} CASCADE;
            DROP TABLE IF EXISTS {outboxTable} CASCADE;
            CREATE TABLE {accountsTable} (
                id BIGINT PRIMARY KEY, 
                balance INT NOT NULL, 
                active BOOLEAN NOT NULL, 
                version BIGINT NOT NULL DEFAULT 1
            );
            CREATE TABLE {outboxTable} (
                seq_id BIGSERIAL PRIMARY KEY, 
                account_id BIGINT NOT NULL, 
                balance INT NOT NULL, 
                active BOOLEAN NOT NULL, 
                created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
            );

            INSERT INTO {accountsTable} (id, balance, active) VALUES 
                (1, 100, true),
                (2, 200, true),
                (3, 300, true);
        ", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task PermanentSequenceGap_SingleRollback_WatermarkAdvancesWithoutStalling()
    {
        string testAccounts = "eap_audit_20261028.test_gap_rollback_single_accounts";
        string testOutbox = "eap_audit_20261028.test_gap_rollback_single_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // 1. Transaction 1: Commits normally (allocates seq 1)
        bool tx1Success = await coord.UpdateAccountAsync(1, 150, true);
        Assert.True(tx1Success);
        Assert.Equal(1, coord.LastAppliedMutationSeq);

        // 2. Transaction 2: Allocates seq 2 in outbox, but rolls back before commit
        coord.OnBeforeCommit = async (id) =>
        {
            if (id == 1)
            {
                throw new InvalidOperationException("Simulated transaction rollback during Tx 2!");
            }
            await Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await coord.UpdateAccountAsync(1, 999, false);
        });

        // Watermark should remain 1, in-flight should be 0 because CancelInFlight was called
        Assert.Equal(1, coord.LastAppliedMutationSeq);
        Assert.Equal(0, coord.InFlightMutationCount);

        // 3. Transaction 3: Commits normally (allocates seq 3)
        coord.OnBeforeCommit = null;
        bool tx3Success = await coord.UpdateAccountAsync(2, 250, true);
        Assert.True(tx3Success);

        // CRITICAL CHECK: Watermark MUST advance to 3 without stalling on rolled-back seq 2!
        Assert.Equal(3, coord.LastAppliedMutationSeq);
        Assert.Equal(0, coord.InFlightMutationCount);

        // 4. Verify in PostgreSQL outbox that seq 2 was never committed
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var checkCmd = new NpgsqlCommand($"SELECT seq_id FROM {testOutbox} ORDER BY seq_id ASC;", conn);
        await using var r = await checkCmd.ExecuteReaderAsync();
        var committedSeqs = new System.Collections.Generic.List<long>();
        while (await r.ReadAsync())
        {
            committedSeqs.Add(r.GetInt64(0));
        }

        // Sequences 1 and 3 committed; sequence 2 does NOT exist in PostgreSQL outbox
        Assert.Equal(new long[] { 1, 3 }, committedSeqs.ToArray());

        // Account 1 was not corrupted by the rolled-back write
        Assert.True(coord.QueryEligible(1)); // balance 150 >= 100
        Assert.True(coord.QueryEligible(2)); // balance 250 >= 100
    }

    [Fact]
    public async Task PermanentSequenceGaps_ConsecutiveRollbacks_And_InterleavedTransactions()
    {
        string testAccounts = "eap_audit_20261028.test_gap_rollback_multi_accounts";
        string testOutbox = "eap_audit_20261028.test_gap_rollback_multi_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Successful Tx 1
        await coord.UpdateAccountAsync(1, 110, true);
        Assert.Equal(1, coord.LastAppliedMutationSeq);

        // Simulate 3 consecutive rolled-back transactions directly in PostgreSQL outbox
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            for (int i = 0; i < 3; i++)
            {
                await using var tx = await conn.BeginTransactionAsync();
                await using var cmd = new NpgsqlCommand($"INSERT INTO {testOutbox} (account_id, balance, active) VALUES (1, 999, false);", conn, tx);
                await cmd.ExecuteNonQueryAsync();
                await tx.RollbackAsync(); // Burns sequence numbers 2, 3, 4!
            }
        }

        // Interleaved successful transaction Tx 5
        await coord.UpdateAccountAsync(2, 220, true);
        // Watermark advances cleanly past burned sequences 2, 3, 4 directly to 5!
        Assert.Equal(5, coord.LastAppliedMutationSeq);

        // Simulate 2 more rolled-back transactions (burning seq 6, 7)
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            for (int i = 0; i < 2; i++)
            {
                await using var tx = await conn.BeginTransactionAsync();
                await using var cmd = new NpgsqlCommand($"INSERT INTO {testOutbox} (account_id, balance, active) VALUES (3, 888, false);", conn, tx);
                await cmd.ExecuteNonQueryAsync();
                await tx.RollbackAsync(); // Burns sequence numbers 6, 7!
            }
        }

        // Successful Tx 8
        await coord.UpdateAccountAsync(3, 330, true);
        Assert.Equal(8, coord.LastAppliedMutationSeq);

        // Verify Process Restart & Recovery
        // A new coordinator hydrating from PostgreSQL must capture snapshot watermark = 8 with 0 missed rows
        var newCoord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        var (processed, watermark, duration) = await newCoord.HydrateSynchronizedFromPostgreSqlAsync();

        Assert.Equal(3, processed);
        Assert.Equal(8, watermark);
        Assert.Equal(8, newCoord.LastAppliedMutationSeq);

        // Check query fidelity after restart
        Assert.True(newCoord.QueryEligible(1)); // 110 >= 100
        Assert.True(newCoord.QueryEligible(2)); // 220 >= 100
        Assert.True(newCoord.QueryEligible(3)); // 330 >= 100
    }

    [Fact]
    public async Task DelayedCommittedEvent_Versus_RollbackGap_Distinction()
    {
        string testAccounts = "eap_audit_20261028.test_delayed_vs_rollback_accounts";
        string testOutbox = "eap_audit_20261028.test_delayed_vs_rollback_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        var writerAPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var crashInjected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Writer A updates Account 1 (becomes seq 1 in outbox).
        // It commits to PostgreSQL, but is paused before memory publication!
        coord.OnBeforePublish = async (id, seq) =>
        {
            if (id == 1 && seq == 1)
            {
                writerAPaused.TrySetResult();
                await crashInjected.Task; // Hold until simulated crash
            }
        };

        var writerATask = Task.Run(async () =>
        {
            try
            {
                await coord.UpdateAccountAsync(1, 50, false); // eligible -> ineligible
            }
            catch
            {
                // Crash simulation
            }
        });

        await writerAPaused.Task;

        // Now, Writer B updates Account 2 (becomes seq 2 in outbox).
        // Writer B commits and publishes!
        coord.OnBeforePublish = null;
        await coord.UpdateAccountAsync(2, 250, true);

        // VERIFICATION:
        // Because Writer A (seq 1) is a GENUINELY COMMITTED delayed event (in-flight),
        // the contiguous watermark CANNOT advance to 2! It MUST remain 0!
        Assert.Equal(0, coord.LastAppliedMutationSeq);
        Assert.Equal(2, coord.HighestPublishedSeq);
        Assert.Equal(1, coord.InFlightMutationCount);

        // Now inject simulated process crash:
        // Cancel / abort Writer A so it never publishes in memory
        coord.MarkHealthy(false);
        crashInjected.TrySetResult();
        await writerATask;

        // In-memory state of Account 1 was NOT updated before crash (remains old balance in bitmap)
        // Now simulate a recovery coordinator restarting:
        var recoveryCoord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        
        // Reconcile from watermark 0:
        // ReconcilePendingMutationsAsync MUST replay both seq 1 and seq 2!
        int replayed = await recoveryCoord.ReconcilePendingMutationsAsync();
        Assert.Equal(2, replayed);
        Assert.Equal(2, recoveryCoord.LastAppliedMutationSeq);

        // Verify that Account 1 was NOT skipped!
        // In PostgreSQL: Account 1 is balance 50, active false (Ineligible)
        // In recoveryCoord: Account 1 must be Ineligible!
        Assert.False(recoveryCoord.QueryEligible(1));
        Assert.True(recoveryCoord.QueryEligible(2)); // 250 >= 100
    }

    [Fact]
    public async Task MetadataPruning_AfterRollbackGaps_DelayedPublications_And_Restarts()
    {
        string testAccounts = "eap_audit_20261028.test_prune_rollback_accounts";
        string testOutbox = "eap_audit_20261028.test_prune_rollback_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // 1. Initial state: 0 metadata entries
        Assert.Equal(0, coord.AccountSequenceMetadataCount);

        // 2. Perform mutations creating gaps
        coord.PublishCrossPlane(1, 150, true, seqId: 10);
        coord.PublishCrossPlane(2, 250, true, seqId: 12); // seq 11 was a rollback gap
        coord.PublishCrossPlane(3, 350, true, seqId: 15); // seq 13, 14 were rollback gaps

        Assert.Equal(3, coord.AccountSequenceMetadataCount);
        Assert.Equal(15, coord.LastAppliedMutationSeq);

        // 3. Prune metadata below watermark 15
        int pruned = coord.PruneMetadataBelowWatermark();
        Assert.Equal(3, pruned);
        Assert.Equal(0, coord.AccountSequenceMetadataCount);

        // 4. Test delayed publication protection:
        // Account 1 receives a delayed write with seqId 9 (older than the watermark 15)
        // Even though Account 1's metadata was pruned, seqId 9 is <= Watermark (15), so it is already in the past
        // But what if a delayed write with seqId 12 arrives for Account 1 while its latest published is 16?
        coord.PublishCrossPlane(1, 400, true, seqId: 16);
        Assert.Equal(1, coord.AccountSequenceMetadataCount);
        Assert.Equal(16, coord.GetAccountPublishedSequence(1));

        // Obsolete write with seq 12 arrives for Account 1:
        bool obsoleteAccepted = coord.PublishCrossPlane(1, 50, false, seqId: 12);
        Assert.False(obsoleteAccepted); // Must be rejected!
        Assert.Equal(16, coord.GetAccountPublishedSequence(1));

        // 5. Prune again after watermark advances to 16
        int prunedAgain = coord.PruneMetadataBelowWatermark();
        Assert.Equal(1, prunedAgain);
        Assert.Equal(0, coord.AccountSequenceMetadataCount);
    }

    [Fact]
    public async Task ShadowPilot_SurvivesRecovery_And_DisablesShadowWhenUnhealthy()
    {
        string testAccounts = "eap_audit_20261028.test_pilot_resilience_accounts";
        string testOutbox = "eap_audit_20261028.test_pilot_resilience_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        using var pilot = new EapShadowPilot(coord, _connectionString, tableName: testAccounts, outboxTable: testOutbox, mismatchThreshold: 3, maxAllowedLag: 5);

        // 1. Normal healthy shadow operation: queries match
        bool decision1 = await pilot.AuthoritativeEvaluateEligibilityAsync(1, 100);
        Assert.True(decision1);

        var t1 = pilot.GetTelemetry();
        Assert.Equal(1, t1.TotalQueries);
        Assert.Equal(1, t1.MatchCount);
        Assert.Equal(0, t1.MismatchCount);

        // 2. Simulate Coordinator failure (IsHealthy = false)
        coord.MarkHealthy(false);

        // Application queries continue serving authoritatively from PostgreSQL without errors!
        bool decisionDuringUnhealthy = await pilot.AuthoritativeEvaluateEligibilityAsync(1, 100);
        Assert.True(decisionDuringUnhealthy);

        // Telemetry check: TotalQueries increased, but MatchCount and MismatchCount did NOT change
        // because shadow execution was safely bypassed while projection was unhealthy!
        var t2 = pilot.GetTelemetry();
        Assert.Equal(2, t2.TotalQueries);
        Assert.Equal(1, t2.MatchCount);
        Assert.Equal(0, t2.MismatchCount);

        // 3. Recovery: Reconcile mutations and restore health
        coord.MarkHealthy(true);

        // Shadow operations resume cleanly
        bool decisionAfterRecovery = await pilot.AuthoritativeEvaluateEligibilityAsync(2, 100);
        Assert.True(decisionAfterRecovery);

        var t3 = pilot.GetTelemetry();
        Assert.Equal(3, t3.TotalQueries);
        Assert.Equal(2, t3.MatchCount);

        // 4. Lag detection: trip circuit breaker when lag exceeds limit
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            // Insert 10 events into outbox without updating EAP
            for (int i = 0; i < 10; i++)
            {
                await using var cmd = new NpgsqlCommand($"INSERT INTO {testOutbox} (account_id, balance, active) VALUES (1, 100, true);", conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        long lag = await pilot.CheckOutboxLagAsync();
        Assert.True(lag >= 10);
        Assert.Equal(CircuitBreakerState.Open, pilot.CircuitState);

        // Even with circuit breaker Open, PostgreSQL authoritative queries continue to succeed!
        bool decisionWhileTripped = await pilot.AuthoritativeEvaluateEligibilityAsync(1, 100);
        Assert.True(decisionWhileTripped);
    }
}
