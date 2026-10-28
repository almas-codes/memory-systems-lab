using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NeuroCache.EapResearch;
using Xunit;

namespace NeuroCache.EapResearch.Tests;

public class EapPublicationRaceTests
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

            INSERT INTO {accountsTable} (id, balance, active) VALUES (1, 200, true);
        ", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task WritePublicationRace_WithoutMonotonicProtection_ProvesStaleOverwriteRace()
    {
        string testAccounts = "eap_audit_20261028.test_race_unpatched_accounts";
        string testOutbox = "eap_audit_20261028.test_race_unpatched_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Turn OFF monotonic protection to reproduce the race
        coord.EnforceMonotonicPublicationOrdering = false;

        var writerAPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerBCanProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerBCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Deterministic test hook: pause Writer A immediately after commit, before memory publication
        coord.OnBeforePublish = async (id, seq) =>
        {
            if (seq == 1) // Writer A
            {
                writerAPaused.TrySetResult();
                await writerBCompleted.Task; // Wait until Writer B has committed and published
            }
        };

        // 1. Writer A: changes Account 1 from eligible (200) to ineligible (50) and commits
        var writerATask = Task.Run(async () =>
        {
            await coord.UpdateAccountAsync(1, newBalance: 50, newActive: false);
        });

        // 2. Wait until Writer A has committed in PostgreSQL but paused before memory publication
        await writerAPaused.Task;

        // 3. Writer B: changes Account 1 from ineligible back to eligible (200), commits, and publishes
        // Temporarily detach hook for Writer B
        coord.OnBeforePublish = null;
        await coord.UpdateAccountAsync(1, newBalance: 200, newActive: true);

        // Notify Writer A that Writer B has committed and completed memory publication
        writerBCompleted.TrySetResult();

        // 4. Resume Writer A and let it attempt publication
        await writerATask;

        // 5. Compare states
        // In PostgreSQL: Account 1 is balance = 200, active = true (Eligible)
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var checkCmd = new NpgsqlCommand($"SELECT balance, active FROM {testAccounts} WHERE id = 1;", conn);
        await using var r = await checkCmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        int dbBalance = r.GetInt32(0);
        bool dbActive = r.GetBoolean(1);
        bool dbEligible = dbBalance >= 100 && dbActive;

        bool bitmapEligible = coord.Bitmap.IsEligible(1);
        bool categoryEligible = coord.CategoryPlane.EvaluateThreshold(1, 100);
        bool queryEligible = coord.QueryEligible(1);

        // Ground truth in DB is ELIGIBLE
        Assert.True(dbEligible, "Database ground truth must be eligible (200, true)");

        // Under unpatched mode, Writer A overwrote memory with stale state!
        // This proves the race condition exists!
        Assert.False(bitmapEligible, "Bitmap was overwritten with stale ineligible state by delayed Writer A!");
        Assert.False(categoryEligible, "Category plane was overwritten with stale state!");
        Assert.False(queryEligible, "Public query returns stale false!");

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task WritePublicationRace_WithMonotonicProtection_GuaranteesCorrectProjection()
    {
        string testAccounts = "eap_audit_20261028.test_race_patched_accounts";
        string testOutbox = "eap_audit_20261028.test_race_patched_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Monotonic publication ordering is ENABLED by default
        Assert.True(coord.EnforceMonotonicPublicationOrdering);

        var writerAPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerBCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        coord.OnBeforePublish = async (id, seq) =>
        {
            if (seq == 1) // Writer A
            {
                writerAPaused.TrySetResult();
                await writerBCompleted.Task; // Wait until Writer B has committed and published
            }
        };

        // 1. Writer A changes Account 1 from eligible to ineligible and commits
        var writerATask = Task.Run(async () =>
        {
            await coord.UpdateAccountAsync(1, newBalance: 50, newActive: false);
        });

        // 2. Pause Writer A immediately after commit but before memory publication
        await writerAPaused.Task;

        // 3. Writer B changes Account 1 to eligible (200, true), commits, and completes memory publication
        coord.OnBeforePublish = null;
        await coord.UpdateAccountAsync(1, newBalance: 200, newActive: true);

        // Signal Writer A to resume
        writerBCompleted.TrySetResult();

        // 4. Resume Writer A and allow it to attempt publication
        await writerATask;

        // 5. Compare final states across DB, Outbox, Bitmap, Category Plane, and Public Query
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var checkCmd = new NpgsqlCommand($"SELECT balance, active FROM {testAccounts} WHERE id = 1;", conn);
        await using var r = await checkCmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        int dbBalance = r.GetInt32(0);
        bool dbActive = r.GetBoolean(1);
        bool dbEligible = dbBalance >= 100 && dbActive;

        bool bitmapEligible = coord.Bitmap.IsEligible(1);
        bool categoryEligible = coord.CategoryPlane.EvaluateThreshold(1, 100);
        bool publicQuery = coord.QueryEligible(1);

        // Outbox check
        await using var outboxConn = new NpgsqlConnection(_connectionString);
        await outboxConn.OpenAsync();
        await using var outboxCmd = new NpgsqlCommand($"SELECT balance, active FROM {testOutbox} ORDER BY seq_id DESC LIMIT 1;", outboxConn);
        await using var outboxReader = await outboxCmd.ExecuteReaderAsync();
        Assert.True(await outboxReader.ReadAsync());
        bool outboxLatestEligible = outboxReader.GetInt32(0) >= 100 && outboxReader.GetBoolean(1);

        // ALL 5 MUST AGREE ON ELIGIBLE (TRUE)
        Assert.True(dbEligible, "Database ground truth must be eligible.");
        Assert.True(outboxLatestEligible, "Latest outbox event must be eligible.");
        Assert.True(bitmapEligible, "Boolean bitmap must remain eligible (Writer A was safely dropped).");
        Assert.True(categoryEligible, "Category plane must remain eligible.");
        Assert.True(publicQuery, "Public QueryEligible must return true.");

        await coord.DisposeAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task WritePublicationRace_ReversedTransitions_MultipleRepetitions(int repetition)
    {
        string testAccounts = $"eap_audit_20261028.test_race_rep_{repetition}_accounts";
        string testOutbox = $"eap_audit_20261028.test_race_rep_{repetition}_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Alternating transition directions based on repetition parity
        bool writerATargetEligible = (repetition % 2 == 1); // True for odd, False for even
        bool writerBTargetEligible = !writerATargetEligible; // Opposite: Writer B always has final committed state

        int writerABalance = writerATargetEligible ? 250 : 30;
        bool writerAActive = writerATargetEligible;

        int writerBBalance = writerBTargetEligible ? 180 : 40;
        bool writerBActive = writerBTargetEligible;

        var writerAPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerBCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        coord.OnBeforePublish = async (id, seq) =>
        {
            if (seq == 1)
            {
                writerAPaused.TrySetResult();
                await writerBCompleted.Task;
            }
        };

        var writerATask = Task.Run(async () =>
        {
            await coord.UpdateAccountAsync(1, writerABalance, writerAActive);
        });

        await writerAPaused.Task;

        coord.OnBeforePublish = null;
        await coord.UpdateAccountAsync(1, writerBBalance, writerBActive);

        writerBCompleted.TrySetResult();
        await writerATask;

        // Final in-memory state must strictly match Writer B's state
        Assert.Equal(writerBTargetEligible, coord.QueryEligible(1));
        Assert.Equal(writerBTargetEligible, coord.Bitmap.IsEligible(1));
        Assert.Equal(writerBTargetEligible, coord.CategoryPlane.EvaluateThreshold(1, 100));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task WritePublicationRace_InsertAndDelete_PreservesMonotonicOrdering()
    {
        string testAccounts = "eap_audit_20261028.test_race_crud_accounts";
        string testOutbox = "eap_audit_20261028.test_race_crud_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(20, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Scenario: Account 5 is inserted by Writer A (eligible), but Writer B immediately deletes it
        // Writer A pauses before publication. Writer B deletes and publishes. Writer A resumes.
        var insertPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        coord.OnBeforePublish = async (id, seq) =>
        {
            if (id == 5 && seq == 1) // Writer A insert
            {
                insertPaused.TrySetResult();
                await deleteCompleted.Task;
            }
        };

        var insertTask = Task.Run(async () =>
        {
            await coord.InsertAccountAsync(5, balance: 250, active: true);
        });

        await insertPaused.Task;

        coord.OnBeforePublish = null;
        await coord.DeleteAccountAsync(5);

        deleteCompleted.TrySetResult();
        await insertTask;

        // Account 5 must be INELIGIBLE (deleted) in both DB and EAP
        Assert.False(coord.QueryEligible(5), "Account 5 must be ineligible after delete publication.");
        Assert.False(coord.Bitmap.IsEligible(5), "Bitmap bit 5 must remain false.");

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task OutboxReplay_OlderEventCannotOverwriteNewerPublishedValue()
    {
        string testAccounts = "eap_audit_20261028.test_replay_order_accounts";
        string testOutbox = "eap_audit_20261028.test_replay_order_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        coord.MarkHealthy(true);

        // Account 1 published at seq_id = 50 (Eligible: balance=200, active=true)
        coord.PublishCrossPlane(1, balance: 200, active: true, seqId: 50);
        Assert.True(coord.QueryEligible(1));
        Assert.Equal(50, coord.GetAccountPublishedSequence(1));

        // Simulate an outbox replay of an older mutation: seq_id = 45 (Ineligible: balance=50, active=false)
        bool applied = coord.PublishCrossPlane(1, balance: 50, active: false, seqId: 45);

        // Monotonic publication order rejects the older event
        Assert.False(applied, "Older sequence 45 must be rejected when account is already at sequence 50.");
        Assert.True(coord.QueryEligible(1), "Account 1 must remain eligible (seq 50 state preserved).");
        Assert.Equal(50, coord.GetAccountPublishedSequence(1));

        // Now simulate a newer mutation: seq_id = 55 (Ineligible)
        bool newerApplied = coord.PublishCrossPlane(1, balance: 50, active: false, seqId: 55);
        Assert.True(newerApplied, "Newer sequence 55 must be applied.");
        Assert.False(coord.QueryEligible(1), "Account 1 must reflect seq 55 state.");
        Assert.Equal(55, coord.GetAccountPublishedSequence(1));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task CrashRecovery_InjectedFailureAfterCommit_FailsClosedUntilReplay()
    {
        string testAccounts = "eap_audit_20261028.test_crash_recovery_accounts";
        string testOutbox = "eap_audit_20261028.test_crash_recovery_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 100, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();
        Assert.True(coord.IsHealthy);

        // Injected failure: throw right after commit, simulating network severance or process crash
        coord.OnBeforePublish = (id, seq) =>
        {
            throw new InvalidOperationException("Simulated host crash immediately after commit!");
        };

        // Mutation commits to PostgreSQL, but publication throws
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await coord.UpdateAccountAsync(1, newBalance: 50, newActive: false);
        });

        // Application MUST NOT be healthy!
        Assert.False(coord.IsHealthy, "Coordinator must fail-closed and mark itself unhealthy after failed publication.");

        // Queries must fail closed and refuse to serve stale data
        Assert.Throws<InvalidOperationException>(() => coord.QueryEligible(1));

        // Recovery: running outbox reconciliation restores projection
        coord.OnBeforePublish = null;
        int replayed = await coord.ReconcilePendingMutationsAsync();
        Assert.True(replayed >= 1, "Reconciliation must replay the unapplied mutation.");

        // Application is healthy again and serves the latest committed projection
        Assert.True(coord.IsHealthy, "Coordinator must become healthy once reconciliation succeeds.");
        Assert.False(coord.QueryEligible(1), "Query now reflects the committed state (ineligible).");

        await coord.DisposeAsync();
    }
}
