using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace NeuroCache.EapResearch;

public interface IEapBenchmarkCandidate : IAsyncDisposable
{
    string Name { get; }
    long EstimatedResidentBytes { get; }
    Task InitializeAsync(long maxId, string connectionString, int threshold = 100, string tableName = "eap_lab.accounts_500m", CancellationToken ct = default);
    ValueTask<bool> QueryEligibleAsync(long id, int threshold = 100, CancellationToken ct = default);
    Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default);
    void ResetCache() { }
}

public sealed class Candidate1_PostgreSqlDirect : IEapBenchmarkCandidate
{
    private string _connStr = "";
    private string _tableName = "eap_lab.accounts_500m";
    public string Name => "PostgreSQL-Direct";
    public long EstimatedResidentBytes => 8 * 1024 * 1024; // ~8MB pooled client memory

    public Task InitializeAsync(long maxId, string connectionString, int threshold = 100, string tableName = "eap_lab.accounts_500m", CancellationToken ct = default)
    {
        _connStr = connectionString;
        _tableName = tableName;
        return Task.CompletedTask;
    }

    public async ValueTask<bool> QueryEligibleAsync(long id, int threshold = 100, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Direct query requiring heap fetch (version is not in covering index)
        await using var cmd = new NpgsqlCommand($"SELECT (balance >= @t AND active AND version >= 0) FROM {_tableName} WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("t", threshold);
        cmd.Parameters.AddWithValue("id", id);

        object? val = await cmd.ExecuteScalarAsync(ct);
        return val is true;
    }

    public async Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"UPDATE {_tableName} SET balance = @b, active = @a, version = version + 1 WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("b", newBalance);
        cmd.Parameters.AddWithValue("a", newActive);
        cmd.Parameters.AddWithValue("id", id);
        return (await cmd.ExecuteNonQueryAsync(ct)) > 0;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class Candidate2_PostgreSqlCovering : IEapBenchmarkCandidate
{
    private string _connStr = "";
    private string _tableName = "eap_lab.accounts_500m";
    public string Name => "PostgreSQL-CoveringIndex";
    public long EstimatedResidentBytes => 8 * 1024 * 1024;

    public Task InitializeAsync(long maxId, string connectionString, int threshold = 100, string tableName = "eap_lab.accounts_500m", CancellationToken ct = default)
    {
        _connStr = connectionString;
        _tableName = tableName;
        return Task.CompletedTask;
    }

    public async ValueTask<bool> QueryEligibleAsync(long id, int threshold = 100, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);

        // Uses covering index if available, or index scan on id
        await using var cmd = new NpgsqlCommand($"SELECT (balance >= @t AND active) FROM {_tableName} WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("t", threshold);
        cmd.Parameters.AddWithValue("id", id);

        object? val = await cmd.ExecuteScalarAsync(ct);
        return val is true;
    }

    public async Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"UPDATE {_tableName} SET balance = @b, active = @a, version = version + 1 WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("b", newBalance);
        cmd.Parameters.AddWithValue("a", newActive);
        cmd.Parameters.AddWithValue("id", id);
        return (await cmd.ExecuteNonQueryAsync(ct)) > 0;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class Candidate3_BoundedResultCache : IEapBenchmarkCandidate
{
    private MemoryCache? _cache;
    private string _connStr = "";
    private string _tableName = "eap_lab.accounts_500m";
    private readonly long _budgetBytes;

    public string Name => "BoundedResultCache";
    public long EstimatedResidentBytes => (long)(_cache?.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0);

    public Candidate3_BoundedResultCache(long budgetBytes = 128 * 1024 * 1024)
    {
        _budgetBytes = budgetBytes;
    }

    public Task InitializeAsync(long maxId, string connectionString, int threshold = 100, string tableName = "eap_lab.accounts_500m", CancellationToken ct = default)
    {
        _connStr = connectionString;
        _tableName = tableName;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _budgetBytes });
        return Task.CompletedTask;
    }

    public async ValueTask<bool> QueryEligibleAsync(long id, int threshold = 100, CancellationToken ct = default)
    {
        if (_cache != null && _cache.TryGetValue(id, out bool cached))
        {
            return cached;
        }

        // Miss -> query Postgres and populate cache
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT (balance >= @t AND active) FROM {_tableName} WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("t", threshold);
        cmd.Parameters.AddWithValue("id", id);
        bool result = (await cmd.ExecuteScalarAsync(ct)) is true;

        if (_cache != null)
        {
            _cache.Set(id, result, new MemoryCacheEntryOptions().SetSize(64)); // ~64B entry
        }

        return result;
    }

    public async Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"UPDATE {_tableName} SET balance = @b, active = @a, version = version + 1 WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("b", newBalance);
        cmd.Parameters.AddWithValue("a", newActive);
        cmd.Parameters.AddWithValue("id", id);
        bool ok = (await cmd.ExecuteNonQueryAsync(ct)) > 0;

        // Invalidate cache
        _cache?.Remove(id);
        return ok;
    }

    public void ResetCache()
    {
        _cache?.Dispose();
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _budgetBytes });
    }

    public ValueTask DisposeAsync()
    {
        _cache?.Dispose();
        _cache = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class Candidate4_WarrantV4 : IEapBenchmarkCandidate
{
    public struct WarrantEntry
    {
        public long Id;
        public uint ClockEpoch;
        public bool Eligible;
    }

    private readonly WarrantEntry[] _table;
    private readonly uint _capacityMask;
    private string _connStr = "";
    private string _tableName = "eap_lab.accounts_500m";
    private uint _globalClock = 1;

    public string Name => "Warrant-v4";
    public long EstimatedResidentBytes => _table.LongLength * 16; // 16B per entry (8B Id + 4B Clock + 1B Bool + 3B padding)

    public Candidate4_WarrantV4(int capacity = 4_194_304) // 4M entries = ~64MB
    {
        int cap = 1;
        while (cap < capacity) cap <<= 1;
        _table = new WarrantEntry[cap];
        _capacityMask = (uint)(cap - 1);
    }

    public Task InitializeAsync(long maxId, string connectionString, int threshold = 100, string tableName = "eap_lab.accounts_500m", CancellationToken ct = default)
    {
        _connStr = connectionString;
        _tableName = tableName;
        return Task.CompletedTask;
    }

    public void ResetCache()
    {
        Array.Clear(_table, 0, _table.Length);
        _globalClock++;
    }

    public async ValueTask<bool> QueryEligibleAsync(long id, int threshold = 100, CancellationToken ct = default)
    {
        uint slot = (uint)id & _capacityMask;
        var entry = _table[slot];

        if (entry.Id == id && entry.ClockEpoch == _globalClock)
        {
            return entry.Eligible;
        }

        // Miss -> query Postgres and populate warrant
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT (balance >= @t AND active) FROM {_tableName} WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("t", threshold);
        cmd.Parameters.AddWithValue("id", id);
        bool result = (await cmd.ExecuteScalarAsync(ct)) is true;

        _table[slot] = new WarrantEntry
        {
            Id = id,
            ClockEpoch = _globalClock,
            Eligible = result
        };

        return result;
    }

    public async Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"UPDATE {_tableName} SET balance = @b, active = @a, version = version + 1 WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("b", newBalance);
        cmd.Parameters.AddWithValue("a", newActive);
        cmd.Parameters.AddWithValue("id", id);
        bool ok = (await cmd.ExecuteNonQueryAsync(ct)) > 0;

        // Invalidate slot
        uint slot = (uint)id & _capacityMask;
        _table[slot] = default;
        return ok;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class Candidate5_EapBooleanBitmap : IEapBenchmarkCandidate
{
    private EapCoordinator? _coord;
    public string Name => "EAP-BooleanBitmap";
    public long EstimatedResidentBytes => _coord?.Bitmap.RawMemoryBytes ?? 0;

    public async Task InitializeAsync(long maxId, string connectionString, int threshold = 100, string tableName = "eap_lab.accounts_500m", CancellationToken ct = default)
    {
        _coord = new EapCoordinator(maxId, connectionString, threshold, tableName);
        // Build bitmap from PostgreSQL
        await _coord.Bitmap.BuildFromPostgreSqlAsync(connectionString, threshold, tableName: tableName, ct: ct);
        _coord.MarkHealthy(true);
    }

    public ValueTask<bool> QueryEligibleAsync(long id, int threshold = 100, CancellationToken ct = default)
    {
        if (_coord == null) return ValueTask.FromResult(false);
        return ValueTask.FromResult(_coord.QueryEligible(id));
    }

    public async Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default)
    {
        if (_coord == null) return false;
        return await _coord.UpdateAccountAsync(id, newBalance, newActive, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_coord != null)
        {
            await _coord.DisposeAsync();
            _coord = null;
        }
    }
}

public sealed class Candidate6_EapPackedCategory : IEapBenchmarkCandidate
{
    private EapCoordinator? _coord;
    public string Name => "EAP-PackedCategory";
    public long EstimatedResidentBytes => _coord?.CategoryPlane.RawMemoryBytes ?? 0;

    public async Task InitializeAsync(long maxId, string connectionString, int threshold = 100, string tableName = "eap_lab.accounts_500m", CancellationToken ct = default)
    {
        _coord = new EapCoordinator(maxId, connectionString, threshold, tableName);
        // Hydrate category plane from PostgreSQL authoritative database
        await _coord.CategoryPlane.BuildFromPostgreSqlAsync(connectionString, tableName: tableName, ct: ct);
        _coord.MarkHealthy(true);
    }

    public ValueTask<bool> QueryEligibleAsync(long id, int threshold = 100, CancellationToken ct = default)
    {
        if (_coord == null) return ValueTask.FromResult(false);
        return ValueTask.FromResult(_coord.QueryThreshold(id, threshold));
    }

    public async Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default)
    {
        if (_coord == null) return false;
        return await _coord.UpdateAccountAsync(id, newBalance, newActive, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_coord != null)
        {
            await _coord.DisposeAsync();
            _coord = null;
        }
    }
}
