using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch;

public sealed class EapDatasetManifest
{
    public string DatasetId { get; set; } = Guid.NewGuid().ToString("N");
    public string DatabaseName { get; set; } = "neurocache_eap_lab";
    public string SchemaName { get; set; } = "eap_lab";
    public string TableName { get; set; } = "accounts_500m";
    public long RowCount { get; set; }
    public long MinimumId { get; set; }
    public long MaximumId { get; set; }
    public string GeneratorVersion { get; set; } = "1.0.0-net10";
    public int GeneratorSeed { get; set; } = 42;
    public string BalanceDistribution { get; set; } = "Uniform [0..199]";
    public string ActiveDistribution { get; set; } = "Bernoulli (p=0.90)";
    public int SchemaVersion { get; set; } = 1;
    public string DurabilityMode { get; set; } = "UNLOGGED";
    public long TableBytes { get; set; }
    public long IndexBytes { get; set; }
    public TimeSpan GenerationDuration { get; set; }
    public string VerificationStatus { get; set; } = "PENDING";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static EapDatasetManifest Load(string path)
    {
        return JsonSerializer.Deserialize<EapDatasetManifest>(File.ReadAllText(path))
               ?? throw new InvalidOperationException("Failed to deserialize manifest");
    }
}

public sealed class EapDatasetGenerator
{
    private readonly string _connectionString;

    public EapDatasetGenerator(string? connectionString = null)
    {
        _connectionString = connectionString ?? PostgreSqlPreflight.GetConnectionString();
    }

    public async Task<EapDatasetManifest> GenerateAsync(
        long totalRecords = 10_000_000L,
        int seed = 42,
        bool unlogged = true,
        string schemaName = "eap_audit_20261028",
        string tableName = "accounts_10m",
        Action<long, long>? progressCallback = null,
        CancellationToken ct = default)
    {
        if (schemaName.Equals("eap_lab", StringComparison.OrdinalIgnoreCase) && 
            (tableName.Equals("accounts_500m", StringComparison.OrdinalIgnoreCase) || tableName.Equals("accounts_pilot", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"SAFETY CHECK FAILED: Refusing to drop or overwrite protected baseline table '{schemaName}.{tableName}'.");
        }

        string fullTableName = $"{schemaName}.{tableName}";
        string idxIdName = $"idx_{tableName}_id";
        string idxCoveringName = $"idx_{tableName}_covering";

        var manifest = new EapDatasetManifest
        {
            SchemaName = schemaName,
            TableName = tableName,
            RowCount = totalRecords,
            MinimumId = 1,
            MaximumId = totalRecords,
            GeneratorSeed = seed,
            DurabilityMode = unlogged ? "UNLOGGED" : "LOGGED"
        };

        var sw = Stopwatch.StartNew();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // 1. Initialize schema and table safely in target schema
        string tableType = unlogged ? "UNLOGGED TABLE" : "TABLE";
        await using (var cmd = new NpgsqlCommand($@"
            CREATE SCHEMA IF NOT EXISTS {schemaName};
            DROP TABLE IF EXISTS {fullTableName} CASCADE;
            CREATE {tableType} {fullTableName} (
                id BIGINT NOT NULL,
                balance INTEGER NOT NULL,
                active BOOLEAN NOT NULL,
                version BIGINT NOT NULL
            );
        ", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 2. High-throughput chunk generation using set-based generate_series
        long chunkSize = 2_000_000L; // 2M rows per chunk for stable batch progress
        long generated = 0;

        while (generated < totalRecords)
        {
            ct.ThrowIfCancellationRequested();

            long startId = generated + 1;
            long currentChunk = Math.Min(chunkSize, totalRecords - generated);
            long endId = generated + currentChunk;

            // Generate deterministic balance and active fields
            string insertSql = $@"
                INSERT INTO {fullTableName} (id, balance, active, version)
                SELECT g,
                       abs((g * 31 + {seed}) % 200)::int,
                       ((g % 10) != 0),
                       1::bigint
                FROM generate_series({startId}, {endId}) AS g;
            ";

            await using (var cmd = new NpgsqlCommand(insertSql, conn))
            {
                cmd.CommandTimeout = 600; // 10 minutes timeout per chunk
                await cmd.ExecuteNonQueryAsync(ct);
            }

            generated += currentChunk;
            progressCallback?.Invoke(generated, totalRecords);
        }

        // 3. Build primary key index on ID and covering index
        await using (var cmd = new NpgsqlCommand($@"
            CREATE UNIQUE INDEX {idxIdName} ON {fullTableName} (id);
            CREATE INDEX {idxCoveringName} ON {fullTableName} (id) INCLUDE (balance, active);
            ANALYZE {fullTableName};
        ", conn))
        {
            cmd.CommandTimeout = 1800; // 30 minutes for indexing
            await cmd.ExecuteNonQueryAsync(ct);
        }

        sw.Stop();
        manifest.GenerationDuration = sw.Elapsed;

        // 4. Measure physical table and index size
        await using (var cmd = new NpgsqlCommand($@"
            SELECT pg_relation_size('{fullTableName}') AS table_bytes,
                   pg_total_relation_size('{fullTableName}') - pg_relation_size('{fullTableName}') AS index_bytes;", conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                manifest.TableBytes = reader.GetInt64(0);
                manifest.IndexBytes = reader.GetInt64(1);
            }
        }

        manifest.VerificationStatus = "GENERATED";
        return manifest;
    }

    public async Task<bool> VerifyAsync(
        long expectedRecords = 10_000_000L, 
        string schemaName = "eap_audit_20261028", 
        string tableName = "accounts_10m", 
        CancellationToken ct = default)
    {
        string fullTableName = $"{schemaName}.{tableName}";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // 1. Verify exact count, min, max
        await using (var cmd = new NpgsqlCommand($@"
            SELECT COUNT(*), MIN(id), MAX(id) FROM {fullTableName};
        ", conn))
        {
            cmd.CommandTimeout = 600;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;

            long count = reader.GetInt64(0);
            long minId = reader.GetInt64(1);
            long maxId = reader.GetInt64(2);

            if (count != expectedRecords || minId != 1 || maxId != expectedRecords)
                return false;
        }

        // 2. Deterministic sample check
        long[] sampleIds = { 1, 100, 1000, expectedRecords / 4, expectedRecords / 2, 3 * expectedRecords / 4, expectedRecords };
        foreach (long id in sampleIds)
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT balance, active, version FROM {fullTableName} WHERE id = @id;
            ", conn);
            cmd.Parameters.AddWithValue("id", id);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;

            int balance = reader.GetInt32(0);
            bool active = reader.GetBoolean(1);
            long version = reader.GetInt64(2);

            if (balance < 0 || balance >= 200 || version != 1)
                return false;
        }

        return true;
    }
}
