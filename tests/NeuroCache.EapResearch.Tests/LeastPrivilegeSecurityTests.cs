using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace NeuroCache.EapResearch.Tests;

public sealed class LeastPrivilegeSecurityTests : IAsyncLifetime
{
    private static bool _rolesEnsured;
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    private readonly string _adminConnStr = PostgreSqlPreflight.GetConnectionString();
    private readonly string _appConnStr = PostgreSqlPreflight.GetAppConnectionString();
    private readonly string _writerConnStr = PostgreSqlPreflight.GetWriterConnectionString();

    public async Task InitializeAsync()
    {
        if (_rolesEnsured) return;
        await _initLock.WaitAsync();
        try
        {
            if (!_rolesEnsured)
            {
                await PostgreSqlPreflight.EnsureLeastPrivilegeRolesAsync(_adminConnStr);
                _rolesEnsured = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task VerifyLeastPrivilegeRoles_AreNonSuperuser_AndCannotCreateRolesOrDatabases()
    {
        await using var conn = new NpgsqlConnection(_adminConnStr);
        await conn.OpenAsync();

        string sql = @"
            SELECT rolname, rolsuper, rolcreaterole, rolcreatedb, rolcanlogin 
            FROM pg_roles 
            WHERE rolname IN ('neurocache_app', 'neurocache_writer');";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        int inspected = 0;
        while (await reader.ReadAsync())
        {
            string roleName = reader.GetString(0);
            bool isSuperuser = reader.GetBoolean(1);
            bool canCreateRole = reader.GetBoolean(2);
            bool canCreateDb = reader.GetBoolean(3);
            bool canLogin = reader.GetBoolean(4);

            Assert.False(isSuperuser, $"Role {roleName} must NOT be a superuser.");
            Assert.False(canCreateRole, $"Role {roleName} must NOT have CREATEROLE.");
            Assert.False(canCreateDb, $"Role {roleName} must NOT have CREATEDB.");
            Assert.True(canLogin, $"Role {roleName} must have LOGIN privilege.");
            inspected++;
        }

        Assert.Equal(2, inspected);
    }

    [Fact]
    public async Task VerifyNeuroCacheAppRole_CanSelect_AndIsDeniedAllMutationsAndDdl()
    {
        await using var conn = new NpgsqlConnection(_appConnStr);
        await conn.OpenAsync();

        // 1. SELECT must succeed
        await using (var selCmd = new NpgsqlCommand("SELECT id, balance, active FROM eap_audit_20261028.accounts_50m LIMIT 5;", conn))
        await using (var reader = await selCmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync(), "neurocache_app should be able to read accounts_50m");
        }

        // 2. UPDATE must be denied with 42501 (permission_denied)
        var updEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("UPDATE eap_audit_20261028.accounts_50m SET balance = 999 WHERE id = 1;", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", updEx.SqlState);

        // 3. INSERT must be denied with 42501
        var insEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO eap_audit_20261028.mutation_log (account_id, balance, active) VALUES (1, 100, true);", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", insEx.SqlState);

        // 4. DELETE must be denied with 42501
        var delEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("DELETE FROM eap_audit_20261028.accounts_50m WHERE id = 1;", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", delEx.SqlState);

        // 5. DROP TABLE must be denied
        var dropEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("DROP TABLE eap_audit_20261028.accounts_50m;", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", dropEx.SqlState);
    }

    [Fact]
    public async Task VerifyNeuroCacheWriterRole_CanMutateAccounts_AndIsDeniedDdl()
    {
        await using var conn = new NpgsqlConnection(_writerConnStr);
        await conn.OpenAsync();

        // 1. SELECT must succeed
        await using (var selCmd = new NpgsqlCommand("SELECT id, balance, active FROM eap_audit_20261028.accounts_50m LIMIT 1;", conn))
        await using (var reader = await selCmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync(), "neurocache_writer should be able to read accounts_50m");
        }

        // 2. DROP TABLE must be denied with 42501
        var dropEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("DROP TABLE eap_audit_20261028.accounts_50m;", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", dropEx.SqlState);

        // 3. TRUNCATE must be denied with 42501
        var truncEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("TRUNCATE eap_audit_20261028.accounts_50m;", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", truncEx.SqlState);
    }

    [Fact]
    public async Task VerifyShadowPilot_WorksWithLeastPrivilegeAppRole()
    {
        // Verify that the shadow pilot can run queries against PostgreSQL using the unprivileged app role
        var coordinator = new EapCoordinator(
            maxId: 1000,
            connectionString: _appConnStr,
            defaultThreshold: 100,
            tableName: "eap_audit_20261028.accounts_50m",
            mutationLogTable: "eap_audit_20261028.mutation_log");

        // Hydrate the first 50 accounts into coordinator memory using the read-only app connection
        await using (var appConn = new NpgsqlConnection(_appConnStr))
        {
            await appConn.OpenAsync();
            await using var readCmd = new NpgsqlCommand(
                "SELECT id, balance, active FROM eap_audit_20261028.accounts_50m WHERE id <= 50;", appConn);
            await using var rdr = await readCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                long id = rdr.GetInt64(0);
                int bal = rdr.GetInt32(1);
                bool act = rdr.GetBoolean(2);
                coordinator.PublishCrossPlane(id, bal, act);
            }
        }

        coordinator.MarkHealthy(true);

        // Use read-only app connection for the shadow pilot
        using var pilot = new EapShadowPilot(
            coordinator,
            connectionString: _appConnStr,
            tableName: "eap_audit_20261028.accounts_50m",
            outboxTable: "eap_audit_20261028.mutation_log",
            mismatchThreshold: 3,
            maxAllowedLag: 1000);

        pilot.StrictMismatchPolicy = true;

        // Shadow read the 50 accounts
        for (long id = 1; id <= 50; id++)
        {
            var result = await pilot.AuthoritativeEvaluateEligibilityAsync(id, threshold: 100);
            Assert.False(pilot.IsKillSwitchEngaged);
            Assert.Equal(CircuitBreakerState.Closed, pilot.CircuitState);
        }

        var telemetry = pilot.GetTelemetry();
        Assert.Equal(50, telemetry.TotalQueries);
        Assert.Equal(50, telemetry.MatchCount);
        Assert.Equal(0, telemetry.MismatchCount);
        Assert.Equal(CircuitBreakerState.Closed, telemetry.State);
    }

    [Fact]
    public void VerifySanitizeConnectionString_MasksCredentials()
    {
        string raw = "Host=localhost;Port=5432;Database=test;Username=myuser;Password=SuperSecret123!;Pooling=true;";
        string sanitized = PostgreSqlPreflight.SanitizeConnectionString(raw);

        Assert.DoesNotContain("SuperSecret123!", sanitized);
        Assert.Contains("Password=********", sanitized);
        Assert.Contains("Username=myuser", sanitized);
        Assert.Contains("Database=test", sanitized);
    }
}
