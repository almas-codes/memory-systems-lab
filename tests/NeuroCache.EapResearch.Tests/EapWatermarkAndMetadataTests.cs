using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NeuroCache.EapResearch;
using Xunit;

namespace NeuroCache.EapResearch.Tests;

public class EapWatermarkAndMetadataTests
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
                (1, 200, true),
                (2, 200, true),
                (3, 50, false);
        ", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CrossAccountWatermarkGap_WithoutContiguousWatermark_DemonstratesSkippedRecoveryAnomaly()
    {
        string testAccounts = "eap_audit_20261028.test_gap_unpatched_accounts";
        string testOutbox = "eap_audit_20261028.test_gap_unpatched_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Turn OFF contiguous watermark to reproduce the cross-account gap anomaly
        coord.EnforceContiguousWatermark = false;

        var writerAPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerBCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pause Writer A after commit, before publication
        coord.OnBeforePublish = async (id, seq) =>
        {
            if (id == 1) // Writer A on Account 1
            {
                writerAPaused.TrySetResult();
                await writerBCompleted.Task;
            }
        };

        // Writer A: updates Account 1 (seq 1), commits, but pauses before memory publication
        var writerATask = Task.Run(async () =>
        {
            await coord.UpdateAccountAsync(1, newBalance: 50, newActive: false);
        });

        await writerAPaused.Task;

        // Writer B: updates Account 2 (seq 2), commits, and publishes
        coord.OnBeforePublish = null;
        await coord.UpdateAccountAsync(2, newBalance: 50, newActive: false);

        // Under unpatched legacy behavior:
        // seq 2 was published, so LastAppliedMutationSeq prematurely jumped to 2!
        long prematureWatermark = coord.LastAppliedMutationSeq;
        Assert.Equal(2, prematureWatermark);

        // Now simulate a crash while Writer A is still pending publication:
        // A recovery coordinator initializing with prematureWatermark = 2 will query:
        // SELECT ... FROM outbox WHERE seq_id > 2
        // which completely SKIPS seq 1 (Account 1)!
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {testOutbox} WHERE seq_id > @wm;", conn);
        cmd.Parameters.AddWithValue("wm", prematureWatermark);
        long missedRecoveries = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        // seq 1 is skipped because watermark jumped to 2
        Assert.Equal(0, missedRecoveries);

        // Clean up writer A
        writerBCompleted.TrySetResult();
        await writerATask;
    }

    [Fact]
    public async Task CrossAccountWatermarkGap_WithContiguousWatermark_PreventsSkippedRecoveryAnomaly()
    {
        string testAccounts = "eap_audit_20261028.test_gap_patched_accounts";
        string testOutbox = "eap_audit_20261028.test_gap_patched_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Enforce contiguous watermark
        coord.EnforceContiguousWatermark = true;

        var writerAPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerBCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pause Writer A after commit, before publication
        coord.OnBeforePublish = async (id, seq) =>
        {
            if (id == 1) // Writer A on Account 1 (seq 1)
            {
                writerAPaused.TrySetResult();
                await writerBCompleted.Task;
            }
        };

        // Writer A: updates Account 1 (seq 1), commits, but pauses before memory publication
        var writerATask = Task.Run(async () =>
        {
            await coord.UpdateAccountAsync(1, newBalance: 50, newActive: false);
        });

        await writerAPaused.Task;

        // Writer B: updates Account 2 (seq 2), commits, and publishes
        coord.OnBeforePublish = null;
        await coord.UpdateAccountAsync(2, newBalance: 50, newActive: false);

        // Under contiguous watermark protection:
        // seq 1 is still in flight!
        // Therefore, ContiguousWatermark CANNOT advance to 2; it remains 0!
        long safeWatermark = coord.LastAppliedMutationSeq;
        Assert.Equal(0, safeWatermark);
        Assert.Equal(2, coord.HighestPublishedSeq);
        Assert.Equal(1, coord.InFlightMutationCount);

        // If a crash happens right now, recovery checks seq_id > 0, catching BOTH seq 1 and seq 2!
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {testOutbox} WHERE seq_id > @wm;", conn);
        cmd.Parameters.AddWithValue("wm", safeWatermark);
        long caughtRecoveries = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        // Both seq 1 and seq 2 are safely captured
        Assert.Equal(2, caughtRecoveries);

        // Unpause Writer A and let it complete publication
        writerBCompleted.TrySetResult();
        await writerATask;

        // Now that Writer A completed publication, the contiguous watermark advances to 2!
        Assert.Equal(2, coord.LastAppliedMutationSeq);
        Assert.Equal(0, coord.InFlightMutationCount);
    }

    [Fact]
    public async Task AccountSequenceMetadata_MemoryScaling_And_SafeReclamation()
    {
        string testAccounts = "eap_audit_20261028.test_meta_reclaim_accounts";
        string testOutbox = "eap_audit_20261028.test_meta_reclaim_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(100, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // 1. Initial hydration: metadata count is 0
        Assert.Equal(0, coord.AccountSequenceMetadataCount);

        // 2. Perform 50 mutations on different account IDs directly through PublishCrossPlane
        for (int i = 1; i <= 50; i++)
        {
            coord.PublishCrossPlane(i, balance: 150, active: true, seqId: i);
        }

        Assert.Equal(50, coord.AccountSequenceMetadataCount);
        Assert.Equal(50, coord.LastAppliedMutationSeq);

        // 3. Execute safe metadata reclamation below watermark
        int pruned = coord.PruneMetadataBelowWatermark();
        Assert.Equal(50, pruned);
        Assert.Equal(0, coord.AccountSequenceMetadataCount);

        // 4. Verify monotonic safety is PRESERVED after pruning:
        // A newer mutation with seq 51 succeeds and registers
        bool acceptedNewer = coord.PublishCrossPlane(1, balance: 250, active: true, seqId: 51);
        Assert.True(acceptedNewer);
        Assert.Equal(1, coord.AccountSequenceMetadataCount);
        Assert.Equal(51, coord.GetAccountPublishedSequence(1));

        // An obsolete mutation with seq 40 (which was superseded and pruned) is safely rejected
        // if another mutation was published, or if arriving out of order compared to current account seq
        bool rejectedObsolete = coord.PublishCrossPlane(1, balance: 10, active: false, seqId: 40);
        Assert.False(rejectedObsolete);
        Assert.Equal(51, coord.GetAccountPublishedSequence(1));
    }

    [Fact]
    public async Task CrashRecovery_FailureBoundaries_FailClosedInvariance()
    {
        string testAccounts = "eap_audit_20261028.test_recovery_boundary_accounts";
        string testOutbox = "eap_audit_20261028.test_recovery_boundary_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Boundary 2: Interrupted pre-publication (Coordinator throws in OnBeforePublish)
        coord.OnBeforePublish = (id, seq) => throw new InvalidOperationException("Simulated process crash pre-publication!");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await coord.UpdateAccountAsync(1, newBalance: 10, newActive: false);
        });

        // Fail-closed invariance: coordinator must be marked unhealthy!
        Assert.False(coord.IsHealthy);

        // Queries MUST throw and reject instead of serving stale or uncoordinated answers
        Assert.Throws<InvalidOperationException>(() => coord.QueryCrossPlaneConsistent(1, 100));

        // Recovery: ReconcilePendingMutations replays the committed outbox event and restores health
        coord.OnBeforePublish = null;
        int replayed = await coord.ReconcilePendingMutationsAsync();
        Assert.True(replayed >= 1);
        Assert.True(coord.IsHealthy);

        // Query now reflects the replayed state
        var (el, catEl) = coord.QueryCrossPlaneConsistent(1, 100);
        Assert.False(el); // 10 < 100 -> ineligible
    }

    [Fact]
    public async Task ShadowPilot_AuthoritativeAndCircuitBreakerBehavior()
    {
        string testAccounts = "eap_audit_20261028.test_pilot_accounts";
        string testOutbox = "eap_audit_20261028.test_pilot_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        using var pilot = new EapShadowPilot(coord, _connectionString, tableName: testAccounts, outboxTable: testOutbox, mismatchThreshold: 3);

        // 1. Authoritative decision matches PostgreSQL
        bool decision = await pilot.AuthoritativeEvaluateEligibilityAsync(1, threshold: 100);
        Assert.True(decision); // Account 1 is balance 200, active true

        var telemetry = pilot.GetTelemetry();
        Assert.Equal(1, telemetry.TotalQueries);
        Assert.Equal(1, telemetry.MatchCount);
        Assert.Equal(0, telemetry.MismatchCount);
        Assert.Equal(CircuitBreakerState.Closed, telemetry.State);

        // 2. Kill switch engagement
        pilot.EngageKillSwitch("Test manual kill switch");
        Assert.True(pilot.IsKillSwitchEngaged);

        // Decisions continue serving authoritatively from PostgreSQL
        bool decisionWithKillSwitch = await pilot.AuthoritativeEvaluateEligibilityAsync(1, threshold: 100);
        Assert.True(decisionWithKillSwitch);

        pilot.DisengageKillSwitch();
        Assert.False(pilot.IsKillSwitchEngaged);

        // 3. Circuit breaker trips on consecutive simulated mismatches
        // Inject intentional mismatch into bitmap without altering PostgreSQL
        coord.Bitmap.SetEligible(1, false);

        for (int i = 0; i < 3; i++)
        {
            await pilot.AuthoritativeEvaluateEligibilityAsync(1, threshold: 100);
        }

        Assert.Equal(CircuitBreakerState.Open, pilot.CircuitState);
        var trippedTelemetry = pilot.GetTelemetry();
        Assert.True(trippedTelemetry.CircuitBreakerTrips >= 1);
        Assert.Equal(3, trippedTelemetry.MismatchCount);

        // Even with circuit breaker open, PostgreSQL authoritative decisions continue reliably!
        bool decisionDuringTrip = await pilot.AuthoritativeEvaluateEligibilityAsync(1, threshold: 100);
        Assert.True(decisionDuringTrip);
    }
}
