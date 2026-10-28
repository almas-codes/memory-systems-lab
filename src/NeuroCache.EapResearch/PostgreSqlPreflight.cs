using System;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch;

public sealed class PreflightReport
{
    public bool Success { get; set; }
    public string ServerVersion { get; set; } = "";
    public string CurrentDatabase { get; set; } = "";
    public string CurrentSchema { get; set; } = "";
    public string TablespaceName { get; set; } = "";
    public string TablespaceLocation { get; set; } = "";
    public long TablespaceFreeBytes { get; set; }
    public long SystemDriveFreeBytes { get; set; }
    public long PilotTableBytes { get; set; }
    public long PilotIndexBytes { get; set; }
    public long Estimated500MTableBytes { get; set; }
    public long Estimated500MIndexBytes { get; set; }
    public long EstimatedTotalRequiredBytes { get; set; }
    public bool CanSafelyHost500M { get; set; }
    public string Recommendation { get; set; } = "";
}

public static class PostgreSqlPreflight
{
    public static string GetConnectionString(string database = "neurocache_eap_lab")
    {
        string? env = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        return $"Host=localhost;Port=5432;Database={database};Username=postgres;Password=admin;Pooling=true;Maximum Pool Size=64;";
    }

    public static async Task<PreflightReport> RunPreflightAsync(CancellationToken ct = default)
    {
        var report = new PreflightReport();
        string connStr = GetConnectionString();

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        report.Success = true;
        report.CurrentDatabase = conn.Database;

        // Check server version
        await using (var cmd = new NpgsqlCommand("SELECT version();", conn))
        {
            report.ServerVersion = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // Check current schema and tablespace
        await using (var cmd = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS eap_lab;", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
            report.CurrentSchema = "eap_lab";
        }

        // Check tablespace location
        await using (var cmd = new NpgsqlCommand(@"
            SELECT spcname, pg_tablespace_location(oid) 
            FROM pg_tablespace 
            WHERE spcname = 'd_tablespace';", conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                report.TablespaceName = reader.GetString(0);
                report.TablespaceLocation = reader.GetString(1);
            }
        }

        // Determine free space on Drive D: and Drive C:
        DriveInfo driveD = new("D");
        report.TablespaceFreeBytes = driveD.AvailableFreeSpace;

        DriveInfo driveC = new("C");
        report.SystemDriveFreeBytes = driveC.AvailableFreeSpace;

        // Run pilot test: 100,000 records to measure exact bytes per row
        await using (var cmd = new NpgsqlCommand(@"
            DROP TABLE IF EXISTS eap_lab.pilot_accounts;
            CREATE UNLOGGED TABLE eap_lab.pilot_accounts (
                id BIGINT NOT NULL,
                balance INTEGER NOT NULL,
                active BOOLEAN NOT NULL,
                version BIGINT NOT NULL
            );
            INSERT INTO eap_lab.pilot_accounts (id, balance, active, version)
            SELECT g, (g % 200)::int, ((g % 10) != 0), 1::bigint
            FROM generate_series(1, 100000) AS g;

            CREATE INDEX idx_pilot_id ON eap_lab.pilot_accounts(id);
        ", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Measure pilot table and index size
        await using (var cmd = new NpgsqlCommand(@"
            SELECT pg_relation_size('eap_lab.pilot_accounts') AS table_bytes,
                   pg_relation_size('eap_lab.idx_pilot_id') AS index_bytes;", conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                report.PilotTableBytes = reader.GetInt64(0);
                report.PilotIndexBytes = reader.GetInt64(1);
            }
        }

        // Drop pilot table
        await using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS eap_lab.pilot_accounts;", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Extrapolate for 500M records (factor of 5,000x over 100k)
        report.Estimated500MTableBytes = report.PilotTableBytes * 5000L;
        report.Estimated500MIndexBytes = report.PilotIndexBytes * 5000L;
        report.EstimatedTotalRequiredBytes = report.Estimated500MTableBytes + report.Estimated500MIndexBytes + (5L * 1024 * 1024 * 1024); // +5GB safety margin

        report.CanSafelyHost500M = report.TablespaceFreeBytes > report.EstimatedTotalRequiredBytes;

        if (report.CanSafelyHost500M)
        {
            report.Recommendation = $"SAFE: Drive D: has {report.TablespaceFreeBytes / (1024 * 1024 * 1024.0):F2} GB free. Estimated 500M dataset requires {report.EstimatedTotalRequiredBytes / (1024 * 1024 * 1024.0):F2} GB.";
        }
        else
        {
            report.Recommendation = $"WARNING: Free space {report.TablespaceFreeBytes / (1024 * 1024 * 1024.0):F2} GB is below estimated requirement {report.EstimatedTotalRequiredBytes / (1024 * 1024 * 1024.0):F2} GB.";
        }

        return report;
    }
}
