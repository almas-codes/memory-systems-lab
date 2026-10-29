using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Data;
using Npgsql;

namespace NeuroCache.ResearchLab.Oracle;

public interface IExactOracle
{
    bool Evaluate(long id, int predicateIndex);
    byte EvaluatePattern(long id);
    AccountRecord? GetRecord(long id);
}

public sealed class PureCodeOracle : IExactOracle
{
    private readonly ConcurrentDictionary<long, AccountRecord> _records = new();

    public void Upsert(long id, int balance, bool active, long version = 1)
    {
        _records[id] = new AccountRecord(id, balance, active, version);
    }

    public AccountRecord? GetRecord(long id)
    {
        return _records.TryGetValue(id, out var r) ? r : null;
    }

    public bool Evaluate(long id, int predicateIndex)
    {
        if (_records.TryGetValue(id, out var r))
        {
            return r.EvaluatePredicate(predicateIndex);
        }
        return false;
    }

    public byte EvaluatePattern(long id)
    {
        if (_records.TryGetValue(id, out var r))
        {
            return r.ComputeAnswerPattern();
        }
        return 0;
    }
}

public sealed class PostgreSqlOracle : IExactOracle
{
    private readonly string _connectionString;
    private readonly string _tableName;

    public PostgreSqlOracle(
        string? connectionString = null,
        string tableName = "eap_audit_20261028.accounts_50m")
    {
        _connectionString = connectionString ?? PostgreSqlPreflight.GetAppConnectionString();
        _tableName = tableName;
    }

    public bool Evaluate(long id, int predicateIndex)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        string pred = predicateIndex switch
        {
            0 => "active",
            1 => "(balance >= 100 AND active)",
            2 => "(balance >= 250 AND active)",
            3 => "(balance >= 500 AND active)",
            4 => "(balance >= 750 AND active)",
            5 => "(balance >= 900 AND active)",
            6 => "(balance >= 500)",
            7 => "(balance >= 100)",
            _ => throw new ArgumentOutOfRangeException(nameof(predicateIndex))
        };
        using var cmd = new NpgsqlCommand($"SELECT {pred} FROM {_tableName} WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        object? val = cmd.ExecuteScalar();
        return val is true;
    }

    public byte EvaluatePattern(long id)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        string sql = $@"
            SELECT 
                ((active::int << 7) | 
                 (((balance >= 100 AND active)::int) << 6) |
                 (((balance >= 250 AND active)::int) << 5) |
                 (((balance >= 500 AND active)::int) << 4) |
                 (((balance >= 750 AND active)::int) << 3) |
                 (((balance >= 900 AND active)::int) << 2) |
                 (((balance >= 500)::int) << 1) |
                 (((balance >= 100)::int) << 0))
            FROM {_tableName} WHERE id = @id;";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        object? val = cmd.ExecuteScalar();
        return val != null && val != DBNull.Value ? Convert.ToByte(val) : (byte)0;
    }

    public AccountRecord? GetRecord(long id)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand($"SELECT id, balance, active, version FROM {_tableName} WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new AccountRecord(reader.GetInt64(0), reader.GetInt32(1), reader.GetBoolean(2), reader.GetInt64(3));
        }
        return null;
    }
}
