using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NeuroCache.EapResearch;
using Xunit;

namespace NeuroCache.EapResearch.Tests;

public class EapShadowPilotFidelityTests
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
                (1, 600, true),
                (2, 200, false),
                (3, 800, true);
        ", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task StrictMismatchPolicy_SingleUnexpectedMismatch_TripsCircuitBreakerImmediately()
    {
        string testAccounts = "eap_audit_20261028.test_pilot_strict_accounts";
        string testOutbox = "eap_audit_20261028.test_pilot_strict_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 500, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        using var pilot = new EapShadowPilot(coord, _connectionString, tableName: testAccounts, outboxTable: testOutbox, mismatchThreshold: 5)
        {
            StrictMismatchPolicy = true
        };

        // Normal query matches
        bool q1 = await pilot.AuthoritativeEvaluateEligibilityAsync(1, 500);
        Assert.True(q1);
        Assert.Equal(CircuitBreakerState.Closed, pilot.CircuitState);

        // Inject intentional single mismatch into bitmap (simulate unexpected drift)
        coord.Bitmap.SetEligible(1, false);

        // Under StrictMismatchPolicy, a single mismatch MUST trip the circuit breaker immediately!
        bool q2 = await pilot.AuthoritativeEvaluateEligibilityAsync(1, 500);
        Assert.True(q2); // PostgreSQL answer still true (authoritative)
        Assert.Equal(CircuitBreakerState.Open, pilot.CircuitState);

        var telemetry = pilot.GetTelemetry();
        Assert.Equal(1, telemetry.MismatchCount);
        Assert.Equal(1, telemetry.CircuitBreakerTrips);
        Assert.Single(telemetry.MismatchAuditTrail);

        var forensic = telemetry.MismatchAuditTrail[0];
        Assert.Equal(1, forensic.AccountId);
        Assert.True(forensic.PgDecision);
        Assert.False(forensic.EapDecision);
    }

    [Fact]
    public async Task NonConsecutiveMismatches_ResetsStreak_AndDoesNotTripPrematurely()
    {
        string testAccounts = "eap_audit_20261028.test_pilot_nonconsec_accounts";
        string testOutbox = "eap_audit_20261028.test_pilot_nonconsec_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 500, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        using var pilot = new EapShadowPilot(coord, _connectionString, tableName: testAccounts, outboxTable: testOutbox, mismatchThreshold: 3)
        {
            StrictMismatchPolicy = false
        };

        // Query 1: Match
        await pilot.AuthoritativeEvaluateEligibilityAsync(1, 500);
        Assert.Equal(CircuitBreakerState.Closed, pilot.CircuitState);

        // Query 2: Mismatch (streak = 1)
        coord.Bitmap.SetEligible(1, false);
        await pilot.AuthoritativeEvaluateEligibilityAsync(1, 500);
        Assert.Equal(CircuitBreakerState.Closed, pilot.CircuitState);

        // Query 3: Match on Account 2 (streak resets to 0!)
        await pilot.AuthoritativeEvaluateEligibilityAsync(2, 500);
        Assert.Equal(CircuitBreakerState.Closed, pilot.CircuitState);

        // Query 4: Mismatch again (streak = 1, not 2!)
        await pilot.AuthoritativeEvaluateEligibilityAsync(1, 500);
        Assert.Equal(CircuitBreakerState.Closed, pilot.CircuitState); // Still closed because streak reset!

        var telemetry = pilot.GetTelemetry();
        Assert.Equal(2, telemetry.MismatchCount);
        Assert.Equal(0, telemetry.CircuitBreakerTrips);
    }

    [Fact]
    public async Task CircuitBreakerReset_FailsClosedWhenUnhealthyOrLagging()
    {
        string testAccounts = "eap_audit_20261028.test_pilot_reset_accounts";
        string testOutbox = "eap_audit_20261028.test_pilot_reset_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 500, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        using var pilot = new EapShadowPilot(coord, _connectionString, tableName: testAccounts, outboxTable: testOutbox, mismatchThreshold: 1, maxAllowedLag: 5);

        // Trip the circuit breaker
        pilot.TripCircuitBreaker("Manual test trip");
        Assert.Equal(CircuitBreakerState.Open, pilot.CircuitState);

        // Condition 1: Coordinator is unhealthy -> Reset MUST be rejected!
        coord.MarkHealthy(false);
        bool resetSuccessWhenUnhealthy = pilot.TryResetCircuitBreaker(out string reasonUnhealthy);
        Assert.False(resetSuccessWhenUnhealthy);
        Assert.Contains("unhealthy", reasonUnhealthy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CircuitBreakerState.Open, pilot.CircuitState);

        // Restore coordinator health
        coord.MarkHealthy(true);

        // Condition 2: Outbox lag exceeds maxAllowedLag (5) -> Reset MUST be rejected!
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            for (int i = 0; i < 10; i++)
            {
                await using var cmd = new NpgsqlCommand($"INSERT INTO {testOutbox} (account_id, balance, active) VALUES (1, 500, true);", conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        bool resetSuccessWhenLagging = pilot.TryResetCircuitBreaker(out string reasonLagging);
        Assert.False(resetSuccessWhenLagging);
        Assert.Contains("lag", reasonLagging, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CircuitBreakerState.Open, pilot.CircuitState);

        // Catch up outbox replay
        await coord.ReconcilePendingMutationsAsync();

        // Condition 3: Healthy AND lag caught up -> Reset succeeds!
        bool resetSuccess = pilot.TryResetCircuitBreaker(out string reasonSuccess);
        Assert.True(resetSuccess);
        Assert.Equal(CircuitBreakerState.Closed, pilot.CircuitState);
    }

    [Fact]
    public async Task KillSwitch_DuringActiveComparisons_CompletelyBypassesShadow()
    {
        string testAccounts = "eap_audit_20261028.test_pilot_ks_accounts";
        string testOutbox = "eap_audit_20261028.test_pilot_ks_outbox";
        await SetupTestTablesAsync(testAccounts, testOutbox);

        var coord = new EapCoordinator(10, _connectionString, defaultThreshold: 500, tableName: testAccounts, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        using var pilot = new EapShadowPilot(coord, _connectionString, tableName: testAccounts, outboxTable: testOutbox);

        // Engage kill switch
        pilot.EngageKillSwitch("Emergency test shutdown");
        Assert.True(pilot.IsKillSwitchEngaged);

        // Queries continue authoritatively via PostgreSQL
        bool decision = await pilot.AuthoritativeEvaluateEligibilityAsync(1, 500);
        Assert.True(decision);

        var telemetry = pilot.GetTelemetry();
        Assert.Equal(1, telemetry.TotalQueries);
        Assert.Equal(0, telemetry.MatchCount); // Shadow comparison was skipped!
        Assert.Equal(0, telemetry.MismatchCount);
        Assert.Equal(1, telemetry.SkippedShadowQueries);
        Assert.Equal(1, telemetry.KillSwitchActivations);
    }

    [Fact]
    public async Task DatabaseWriteBoundary_ProvesUnauthorizedRoleDenied_AndSuperuserBypasses()
    {
        string testTable = "eap_audit_20261028.test_perm_boundary_accounts";
        string testRole = "neurocache_test_unauthorized_reader";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // 1. Inspect current connection role
        await using var whoCmd = new NpgsqlCommand("SELECT current_user, rolsuper FROM pg_roles WHERE rolname = current_user;", conn);
        await using var whoReader = await whoCmd.ExecuteReaderAsync();
        Assert.True(await whoReader.ReadAsync());
        string currentUser = whoReader.GetString(0);
        bool isSuperuser = whoReader.GetBoolean(1);
        await whoReader.CloseAsync();

        // Current application connection connects as postgres (superuser)
        Assert.Equal("postgres", currentUser);
        Assert.True(isSuperuser);

        // 2. Create isolated test role without superuser privileges
        await using var setupCmd = new NpgsqlCommand($@"
            DROP TABLE IF EXISTS {testTable} CASCADE;
            CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL);
            INSERT INTO {testTable} VALUES (1, 100, true);

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{testRole}') THEN
                    CREATE ROLE {testRole} WITH LOGIN PASSWORD 'TestPass123!';
                END IF;
            END
            $$;

            GRANT CONNECT ON DATABASE neurocache_eap_lab TO {testRole};
            GRANT USAGE ON SCHEMA eap_audit_20261028 TO {testRole};
            GRANT SELECT ON {testTable} TO {testRole};
            REVOKE INSERT, UPDATE, DELETE ON {testTable} FROM {testRole};
        ", conn);
        await setupCmd.ExecuteNonQueryAsync();

        // 3. Test that unauthorized role is strictly DENIED write operations
        string unauthorizedConnStr = $"Host=localhost;Port=5432;Database=neurocache_eap_lab;Username={testRole};Password=TestPass123!;Pooling=false;";
        await using (var unauthConn = new NpgsqlConnection(unauthorizedConnStr))
        {
            await unauthConn.OpenAsync();

            // Read query succeeds
            await using var readCmd = new NpgsqlCommand($"SELECT balance FROM {testTable} WHERE id = 1;", unauthConn);
            object? val = await readCmd.ExecuteScalarAsync();
            Assert.Equal(100, Convert.ToInt32(val));

            // Write operation fails with 42501 (permission denied)
            await using var writeCmd = new NpgsqlCommand($"UPDATE {testTable} SET balance = 999 WHERE id = 1;", unauthConn);
            var ex = await Assert.ThrowsAsync<PostgresException>(async () => await writeCmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState); // 42501 = permission_denied
        }

        // 4. Test that the application's current role (postgres) can write because it is superuser
        await using var superWriteCmd = new NpgsqlCommand($"UPDATE {testTable} SET balance = 500 WHERE id = 1;", conn);
        int rows = await superWriteCmd.ExecuteNonQueryAsync();
        Assert.Equal(1, rows);

        // Cleanup
        await using var cleanCmd = new NpgsqlCommand($@"
            DROP TABLE IF EXISTS {testTable} CASCADE;
            DROP OWNED BY {testRole} CASCADE;
            DROP ROLE IF EXISTS {testRole};
        ", conn);
        await cleanCmd.ExecuteNonQueryAsync();
    }
}
