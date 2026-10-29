using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch.AdaptiveCompression;

/// <summary>
/// Candidate E: Direct PostgreSQL Baseline.
/// Executes queries directly against PostgreSQL authoritative table.
/// Measures baseline disk-backed relational query latency and throughput.
/// </summary>
public sealed class CandidateE_PostgreSqlBaseline
{
    private readonly string _connectionString;
    private readonly string _tableName;

    public CandidateE_PostgreSqlBaseline(
        string? connectionString = null,
        string tableName = "eap_audit_20261028.accounts_50m")
    {
        _connectionString = connectionString ?? PostgreSqlPreflight.GetAppConnectionString();
        _tableName = tableName;
    }

    public async Task<bool> QuerySingleAsync(long id, int queryIndex, CancellationToken ct = default)
    {
        string pred = QueryFamily.QuerySqlPredicates[queryIndex];
        string sql = $"SELECT {pred} FROM {_tableName} WHERE id = @id;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        object? val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return val is true;
    }

    public async Task<byte> QueryAllAsync(long id, CancellationToken ct = default)
    {
        string sql = $@"
            SELECT 
                ({QueryFamily.QuerySqlPredicates[0]})::int AS q1,
                ({QueryFamily.QuerySqlPredicates[1]})::int AS q2,
                ({QueryFamily.QuerySqlPredicates[2]})::int AS q3,
                ({QueryFamily.QuerySqlPredicates[3]})::int AS q4,
                ({QueryFamily.QuerySqlPredicates[4]})::int AS q5,
                ({QueryFamily.QuerySqlPredicates[5]})::int AS q6,
                ({QueryFamily.QuerySqlPredicates[6]})::int AS q7,
                ({QueryFamily.QuerySqlPredicates[7]})::int AS q8
            FROM {_tableName}
            WHERE id = @id;";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            int p = 0;
            for (int i = 0; i < 8; i++)
            {
                if (reader.GetInt32(i) == 1)
                {
                    p |= (1 << (7 - i));
                }
            }
            return (byte)p;
        }
        return 0;
    }
}
