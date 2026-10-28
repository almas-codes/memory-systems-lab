using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch;

public sealed class EapCoordinator : IAsyncDisposable
{
    private const int ShardCount = 1024;
    private readonly SemaphoreSlim[] _shardLocks = new SemaphoreSlim[ShardCount];
    private readonly object[] _shardWriterLocks = new object[ShardCount];
    private readonly long[] _shardVersions = new long[ShardCount];
    private readonly string _connectionString;
    private readonly string _tableName;
    private readonly string _mutationLogTable;
    private readonly EapBooleanBitmap _bitmap;
    private readonly EapPackedCategoryPlane _categoryPlane;
    private readonly int _defaultThreshold;
    private long _lastAppliedMutationSeq;

    public bool IsHealthy { get; private set; } = false;
    public long LastAppliedMutationSeq => Volatile.Read(ref _lastAppliedMutationSeq);
    public EapBooleanBitmap Bitmap => _bitmap;
    public EapPackedCategoryPlane CategoryPlane => _categoryPlane;

    public void MarkHealthy(bool healthy = true) => IsHealthy = healthy;

    public EapCoordinator(
        long maxId, 
        string connectionString, 
        int defaultThreshold = 100, 
        string tableName = "eap_lab.accounts_500m",
        string mutationLogTable = "eap_audit_20261028.mutation_log")
    {
        _connectionString = connectionString;
        _tableName = tableName;
        _mutationLogTable = mutationLogTable;
        _defaultThreshold = defaultThreshold;
        _bitmap = new EapBooleanBitmap(maxId);
        _categoryPlane = new EapPackedCategoryPlane(maxId);

        for (int i = 0; i < ShardCount; i++)
        {
            _shardLocks[i] = new SemaphoreSlim(1, 1);
            _shardWriterLocks[i] = new object();
            _shardVersions[i] = 0;
        }
    }

    private int GetShard(long id) => (int)((ulong)id % (ulong)ShardCount);

    public bool QueryEligible(long id)
    {
        if (!IsHealthy)
            throw new InvalidOperationException("EAP Coordinator is in unhealthy/desynchronized state; queries must fail-closed or fall back to PostgreSQL.");

        return _bitmap.IsEligible(id);
    }

    public bool QueryThreshold(long id, int threshold)
    {
        if (!IsHealthy)
            throw new InvalidOperationException("EAP Coordinator is in unhealthy/desynchronized state; queries must fail-closed or fall back to PostgreSQL.");

        var detailed = _categoryPlane.EvaluateThresholdDetailed(id, threshold);
        if (detailed == CategoryEvaluationResult.DefiniteTrue)
            return true;
        if (detailed == CategoryEvaluationResult.DefiniteFalse)
            return false;

        // Ambiguous bucket: fall back to PostgreSQL authoritative ground truth
        return QueryPostgreSqlExact(id, threshold);
    }

    public CategoryEvaluationResult QueryThresholdDetailed(long id, int threshold)
    {
        if (!IsHealthy)
            throw new InvalidOperationException("EAP Coordinator is in unhealthy state.");

        return _categoryPlane.EvaluateThresholdDetailed(id, threshold);
    }

    public bool QueryPostgreSqlExact(long id, int threshold)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand($"SELECT (balance >= @t AND active) FROM {_tableName} WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("t", threshold);
        cmd.Parameters.AddWithValue("id", id);
        object? val = cmd.ExecuteScalar();
        return val is true;
    }

    /// <summary>
    /// Publishes an in-memory update across both the Boolean plane and Category plane under Seqlock.
    /// Serialized per-shard via _shardWriterLocks to prevent concurrent writers from corrupting
    /// the sequence counter and exposing torn states to readers.
    /// </summary>
    public void PublishCrossPlane(long id, int balance, bool active)
    {
        int shard = GetShard(id);
        lock (_shardWriterLocks[shard])
        {
            Interlocked.Increment(ref _shardVersions[shard]); // Turn odd (write in progress)
            try
            {
                bool eligible = (balance >= _defaultThreshold) && active;
                _bitmap.SetEligible(id, eligible);
                _categoryPlane.SetAccount(id, balance, active);
            }
            finally
            {
                Interlocked.Increment(ref _shardVersions[shard]); // Turn even (published)
            }
        }
    }

    /// <summary>
    /// Evaluates both the Boolean eligibility plane and the Category plane atomically
    /// using a non-blocking Seqlock, preventing torn cross-plane reads.
    /// Includes starvation mitigation if writes are heavily sustained.
    /// </summary>
    public (bool Eligible, bool CategoryEligible) QueryCrossPlaneConsistent(long id, int threshold)
    {
        if (!IsHealthy)
            throw new InvalidOperationException("EAP Coordinator is in unhealthy state.");

        int shard = GetShard(id);
        long v1 = 0, v2 = 0;
        bool el = false;
        bool catEl = false;
        int spins = 0;

        do
        {
            v1 = Volatile.Read(ref _shardVersions[shard]);
            if ((v1 & 1) != 0)
            {
                Thread.SpinWait(4);
                if (++spins > 100) Thread.Yield();
                if (spins > 500)
                {
                    // Reader starvation mitigation: acquire the shard writer lock to guarantee progress
                    lock (_shardWriterLocks[shard])
                    {
                        el = _bitmap.IsEligible(id);
                        catEl = _categoryPlane.EvaluateThreshold(id, threshold);
                        return (el, catEl);
                    }
                }
                continue;
            }

            el = _bitmap.IsEligible(id);
            catEl = _categoryPlane.EvaluateThreshold(id, threshold);

            v2 = Volatile.Read(ref _shardVersions[shard]);
        } while (v1 != v2 || (v1 & 1) != 0);

        return (el, catEl);
    }

    public async Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default)
    {
        int shard = GetShard(id);
        var sem = _shardLocks[shard];

        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long mutationSeq = 0;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Advisory transaction lock: ensures outbox insertions and commits occur in strict monotonic order,
            // preventing the outbox sequence-gap anomaly where concurrent uncommitted transactions are skipped.
            try
            {
                await using var advCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('eap_outbox_lock'));", conn, tx);
                await advCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Fall back if advisory locks are restricted in the session
            }

            // 1. Authoritative write to PostgreSQL table
            string updateSql = $@"
                UPDATE {_tableName} 
                SET balance = @b, active = @a, version = version + 1 
                WHERE id = @id;";

            await using (var cmd = new NpgsqlCommand(updateSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("b", newBalance);
                cmd.Parameters.AddWithValue("a", newActive);
                cmd.Parameters.AddWithValue("id", id);

                int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }
            }

            // 2. Insert into transactional mutation log (outbox) if available
            try
            {
                string logSql = $@"
                    INSERT INTO {_mutationLogTable} (account_id, balance, active) 
                    VALUES (@id, @b, @a) 
                    RETURNING seq_id;";

                await using var logCmd = new NpgsqlCommand(logSql, conn, tx);
                logCmd.Parameters.AddWithValue("id", id);
                logCmd.Parameters.AddWithValue("b", newBalance);
                logCmd.Parameters.AddWithValue("a", newActive);

                object? seqVal = await logCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (seqVal != null && seqVal != DBNull.Value)
                {
                    mutationSeq = Convert.ToInt64(seqVal);
                }
            }
            catch
            {
                // If mutation log table doesn't exist, proceed with transaction
            }

            // Commit transaction authoritatively
            await tx.CommitAsync(ct).ConfigureAwait(false);

            // 3. Publish to in-memory answer planes under Seqlock
            PublishCrossPlane(id, newBalance, newActive);
            if (mutationSeq > 0)
            {
                Interlocked.Exchange(ref _lastAppliedMutationSeq, mutationSeq);
            }

            return true;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Reconciles and replays any pending mutations committed to PostgreSQL that have not yet
    /// been applied to the in-memory planes (e.g. after a process crash or exception).
    /// </summary>
    public async Task<int> ReconcilePendingMutationsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        string query = $@"
            SELECT seq_id, account_id, balance, active 
            FROM {_mutationLogTable} 
            WHERE seq_id > @lastSeq 
            ORDER BY seq_id ASC;";

        await using var cmd = new NpgsqlCommand(query, conn);
        cmd.Parameters.AddWithValue("lastSeq", Volatile.Read(ref _lastAppliedMutationSeq));

        int replayed = 0;
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long seq = reader.GetInt64(0);
                long accountId = reader.GetInt64(1);
                int balance = reader.GetInt32(2);
                bool active = reader.GetBoolean(3);

                int shard = GetShard(accountId);
                lock (_shardWriterLocks[shard])
                {
                    Interlocked.Increment(ref _shardVersions[shard]);
                    try
                    {
                        bool eligible = (balance >= _defaultThreshold) && active;
                        _bitmap.SetEligible(accountId, eligible);
                        _categoryPlane.SetAccount(accountId, balance, active);
                        Interlocked.Exchange(ref _lastAppliedMutationSeq, seq);
                    }
                    finally
                    {
                        Interlocked.Increment(ref _shardVersions[shard]);
                    }
                }
                replayed++;
            }

            IsHealthy = true;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table does not exist
        }

        return replayed;
    }

    /// <summary>
    /// Performs an initial synchronized snapshot hydration from PostgreSQL while maintaining
    /// exact watermark synchronization with the transactional outbox log.
    /// </summary>
    public async Task<(long ProcessedRecords, long Watermark, TimeSpan Duration)> HydrateSynchronizedFromPostgreSqlAsync(
        Action<long, long>? progressCallback = null,
        Func<long, Task>? onSnapshotAcquired = null,
        Func<long, Task>? onRowProcessed = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Execute in REPEATABLE READ snapshot to obtain atomic coordination between the table and outbox
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        // 1. Capture snapshot watermark
        long snapshotWatermark = 0;
        try
        {
            await using var wmCmd = new NpgsqlCommand($"SELECT COALESCE(MAX(seq_id), 0) FROM {_mutationLogTable};", conn, tx);
            object? val = await wmCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (val != null && val != DBNull.Value)
                snapshotWatermark = Convert.ToInt64(val);
        }
        catch
        {
            snapshotWatermark = 0;
        }

        // Test hook: allows deterministic mutations to be performed immediately after snapshot isolation watermark is acquired
        if (onSnapshotAcquired != null)
        {
            await onSnapshotAcquired(snapshotWatermark).ConfigureAwait(false);
        }

        // 2. Stream table rows under identical snapshot
        string query = $"SELECT id, balance, active FROM {_tableName};";
        await using var cmd = new NpgsqlCommand(query, conn, tx);
        cmd.CommandTimeout = 1800;

        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);

        long processed = 0;
        long maxId = _bitmap.MaxId;
        try
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long id = reader.GetInt64(0);
                int balance = reader.GetInt32(1);
                bool active = reader.GetBoolean(2);

                if (id <= 0 || (ulong)(id - 1) >= (ulong)maxId)
                {
                    IsHealthy = false;
                    throw new InvalidOperationException($"Hydration encountered account ID {id} which exceeds allocated capacity {maxId}. A partial plane cannot be considered ready.");
                }

                bool eligible = (balance >= _defaultThreshold) && active;
                _bitmap.SetEligible(id, eligible);
                _categoryPlane.SetAccount(id, balance, active);

                processed++;
                if (onRowProcessed != null)
                {
                    await onRowProcessed(processed).ConfigureAwait(false);
                }
                if (progressCallback != null && processed % 1_000_000 == 0)
                {
                    progressCallback(processed, maxId);
                }
            }

            await reader.CloseAsync().ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            // Record initial watermark
            Interlocked.Exchange(ref _lastAppliedMutationSeq, snapshotWatermark);

            // 3. Catch up any mutations committed after the snapshot transaction began
            int replayed = await ReconcilePendingMutationsAsync(ct).ConfigureAwait(false);

            IsHealthy = true;
            sw.Stop();
            return (processed, snapshotWatermark + replayed, sw.Elapsed);
        }
        catch
        {
            IsHealthy = false;
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        for (int i = 0; i < ShardCount; i++)
        {
            _shardLocks[i].Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
