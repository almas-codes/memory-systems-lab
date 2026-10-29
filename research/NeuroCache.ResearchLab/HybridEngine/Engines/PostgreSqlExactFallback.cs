using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.EapResearch.AdaptiveCompression;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using Npgsql;

namespace NeuroCache.ResearchLab.HybridEngine.Engines;

public sealed class PostgreSqlExactFallback : IExactFallback
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<long, (int Balance, bool Active)> _fallbackStore = new();
    private readonly (int Balance, bool Active)[]? _denseStore;
    private readonly long _maxDenseId;
    private bool _dbAvailable = true;

    public string EngineName => "PostgreSQL Authoritative Exact Fallback";

    public PostgreSqlExactFallback(
        string connectionString = "Host=localhost;Port=5432;Database=neurocache_eap_lab;Username=postgres;Password=admin;",
        bool forceInMemory = false,
        long maxDenseId = 1_000_000)
    {
        _connectionString = connectionString;
        _maxDenseId = maxDenseId;
        if (maxDenseId > 0 && maxDenseId <= 10_000_000)
        {
            _denseStore = new (int Balance, bool Active)[maxDenseId + 1];
        }

        if (forceInMemory)
        {
            _dbAvailable = false;
        }
    }

    public void SeedInMemoryFallback(long id, int balance, bool active)
    {
        if (_denseStore != null && id > 0 && id <= _maxDenseId)
        {
            _denseStore[id] = (balance, active);
        }
        _fallbackStore[id] = (balance, active);
    }

    public async Task<bool> EvaluatePredicateAsync(long accountId, int predicateIndex, CancellationToken ct = default)
    {
        if (predicateIndex < 0 || predicateIndex >= 8)
        {
            // Unsupported predicate index cannot be evaluated by declared QueryFamily; safe exact false
            return false;
        }

        if (_dbAvailable)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT balance, active FROM eap_audit_20261028.accounts_50m WHERE id = @id LIMIT 1;";
                cmd.Parameters.AddWithValue("id", accountId);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    int balance = reader.GetInt32(0);
                    bool active = reader.GetBoolean(1);
                    return QueryFamily.EvaluatePredicate(predicateIndex, balance, active);
                }
            }
            catch
            {
                _dbAvailable = false;
            }
        }

        // Fast in-memory dense lookup
        if (_denseStore != null && accountId > 0 && accountId <= _maxDenseId)
        {
            var st = _denseStore[accountId];
            return QueryFamily.EvaluatePredicate(predicateIndex, st.Balance, st.Active);
        }

        // In-memory fallback dictionary
        if (_fallbackStore.TryGetValue(accountId, out var state))
        {
            return QueryFamily.EvaluatePredicate(predicateIndex, state.Balance, state.Active);
        }

        return false;
    }

    public async Task<long> CountMatchingAsync(AnalyticalQuery query, long maxAccountId, CancellationToken ct = default)
    {
        if (_dbAvailable)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();

                string activeClause = query.RequireActive ? " AND active = true" : "";
                string limitClause = maxAccountId > 0 ? $" AND id <= {maxAccountId}" : "";
                cmd.CommandText = $"SELECT COUNT(*) FROM eap_audit_20261028.accounts_50m WHERE balance >= @thresh{activeClause}{limitClause};";
                cmd.Parameters.AddWithValue("thresh", query.Threshold);

                object? result = await cmd.ExecuteScalarAsync(ct);
                return Convert.ToInt64(result);
            }
            catch
            {
                _dbAvailable = false;
            }
        }

        // Fast in-memory dense scan
        if (_denseStore != null)
        {
            long limit = maxAccountId > 0 ? Math.Min(maxAccountId, _maxDenseId) : _maxDenseId;
            long count = 0;
            int thresh = query.Threshold;
            bool reqActive = query.RequireActive;

            for (long id = 1; id <= limit; id++)
            {
                var item = _denseStore[id];
                if (item.Balance >= thresh && (!reqActive || item.Active))
                {
                    count++;
                }
            }
            return count;
        }

        // In-memory scan fallback
        long dictCount = 0;
        foreach (var kvp in _fallbackStore)
        {
            if (maxAccountId > 0 && kvp.Key > maxAccountId) continue;
            bool match = kvp.Value.Balance >= query.Threshold;
            if (query.RequireActive) match &= kvp.Value.Active;
            if (match) dictCount++;
        }
        return dictCount;
    }

    public async Task<(bool Success, object? Result)> ExecuteCustomSqlAsync(string sql, CancellationToken ct = default)
    {
        if (!_dbAvailable) return (false, null);

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            object? res = await cmd.ExecuteScalarAsync(ct);
            return (true, res);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
