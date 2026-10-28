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
    private long _legacyLastAppliedMutationSeq;
    private readonly ContiguousWatermarkTracker _watermarkTracker = new();

    public bool IsHealthy { get; private set; } = false;
    
    /// <summary>
    /// When EnforceContiguousWatermark is true, returns the contiguous applied watermark
    /// (the highest sequence such that all preceding sequences have completed memory publication).
    /// When false, returns the legacy unconstrained maximum sequence.
    /// </summary>
    public long LastAppliedMutationSeq => EnforceContiguousWatermark 
        ? _watermarkTracker.Watermark 
        : Volatile.Read(ref _legacyLastAppliedMutationSeq);

    public long ContiguousWatermark => _watermarkTracker.Watermark;
    public long HighestPublishedSeq => _watermarkTracker.HighestPublishedSeq;
    public int InFlightMutationCount => _watermarkTracker.InFlightCount;
    public int AccountSequenceMetadataCount => _accountSequences.Count;

    public EapBooleanBitmap Bitmap => _bitmap;
    public EapPackedCategoryPlane CategoryPlane => _categoryPlane;

    // Deterministic test hooks for concurrency and gate verification
    public Func<long, Task>? OnBeforeAcquireDbLock { get; set; }
    public Func<long, Task>? OnAfterAcquireDbLock { get; set; }
    public Func<long, Task>? OnBeforeCommit { get; set; }
    public Func<long, long, Task>? OnAfterCommit { get; set; }
    public Func<long, long, Task>? OnBeforePublish { get; set; }
    public Func<long, long, Task>? OnAfterPublish { get; set; }

    /// <summary>
    /// When true, enforces monotonic publication ordering per account: an older committed mutation
    /// that publishes late will NEVER overwrite a newer committed mutation that already published.
    /// Can be toggled in adversarial tests to prove both the vulnerability and the fix.
    /// </summary>
    public bool EnforceMonotonicPublicationOrdering { get; set; } = true;

    /// <summary>
    /// When true, guarantees that the global last-applied watermark never advances past
    /// any in-flight mutation that has committed to PostgreSQL but not yet completed memory publication.
    /// Can be toggled in adversarial tests to prove both the watermark gap anomaly and the fix.
    /// </summary>
    public bool EnforceContiguousWatermark { get; set; } = true;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _accountSequences = new();

    public long GetAccountPublishedSequence(long id) => _accountSequences.TryGetValue(id, out long seq) ? seq : 0;

    /// <summary>
    /// Prunes entries from _accountSequences whose sequence number is less than or equal to
    /// the contiguous applied watermark. Once the global watermark has reached W, no event with
    /// sequence &lt;= W can ever arrive, making those entries completely safe to reclaim.
    /// </summary>
    public int PruneMetadataBelowWatermark()
    {
        long wm = LastAppliedMutationSeq;
        if (wm <= 0) return 0;

        int pruned = 0;
        foreach (var kvp in _accountSequences)
        {
            if (kvp.Value <= wm)
            {
                if (_accountSequences.TryRemove(kvp.Key, out _))
                {
                    pruned++;
                }
            }
        }
        return pruned;
    }

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
    /// When EnforceMonotonicPublicationOrdering is enabled, an older mutation sequence number
    /// is rejected and dropped if a newer mutation sequence has already been published for this account.
    /// </summary>
    public bool PublishCrossPlane(long id, int balance, bool active, long seqId = 0)
    {
        int shard = GetShard(id);
        lock (_shardWriterLocks[shard])
        {
            if (EnforceMonotonicPublicationOrdering && seqId > 0)
            {
                if (_accountSequences.TryGetValue(id, out long currentSeq) && seqId < currentSeq)
                {
                    // Superseded mutation: a newer mutation was already published for this account.
                    // Dropping obsolete write to prevent silent memory corruption.
                    if (EnforceContiguousWatermark)
                    {
                        _watermarkTracker.AcknowledgePublication(seqId);
                    }
                    return false;
                }
            }

            Interlocked.Increment(ref _shardVersions[shard]); // Turn odd (write in progress)
            try
            {
                bool eligible = (balance >= _defaultThreshold) && active;
                _bitmap.SetEligible(id, eligible);
                _categoryPlane.SetAccount(id, balance, active);

                if (seqId > 0)
                {
                    _accountSequences[id] = seqId;
                }
            }
            finally
            {
                Interlocked.Increment(ref _shardVersions[shard]); // Turn even (published)
            }
        }

        if (seqId > 0)
        {
            if (EnforceContiguousWatermark)
            {
                _watermarkTracker.AcknowledgePublication(seqId);
            }
            else
            {
                long currentLast;
                do
                {
                    currentLast = Volatile.Read(ref _legacyLastAppliedMutationSeq);
                    if (seqId <= currentLast) break;
                } while (Interlocked.CompareExchange(ref _legacyLastAppliedMutationSeq, seqId, currentLast) != currentLast);
            }
        }

        return true;
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
        long mutationSeq = 0;
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            if (OnBeforeAcquireDbLock != null)
                await OnBeforeAcquireDbLock(id).ConfigureAwait(false);

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

            if (OnAfterAcquireDbLock != null)
                await OnAfterAcquireDbLock(id).ConfigureAwait(false);

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
                    if (EnforceContiguousWatermark)
                    {
                        _watermarkTracker.RegisterInFlight(mutationSeq);
                    }
                }
            }
            catch
            {
                // If mutation log table doesn't exist, proceed with transaction
            }

            if (OnBeforeCommit != null)
                await OnBeforeCommit(id).ConfigureAwait(false);

            // Commit transaction authoritatively
            await tx.CommitAsync(ct).ConfigureAwait(false);

            if (OnAfterCommit != null)
                await OnAfterCommit(id, mutationSeq).ConfigureAwait(false);
        }
        catch
        {
            if (EnforceContiguousWatermark && mutationSeq > 0)
            {
                _watermarkTracker.CancelInFlight(mutationSeq);
            }
            throw;
        }
        finally
        {
            // Release the shard DB lock once the transaction commits authoritatively in PostgreSQL
            sem.Release();
        }

        // 3. Publish to in-memory answer planes under Seqlock with monotonic sequence checking
        try
        {
            if (OnBeforePublish != null)
                await OnBeforePublish(id, mutationSeq).ConfigureAwait(false);

            PublishCrossPlane(id, newBalance, newActive, mutationSeq);

            if (OnAfterPublish != null)
                await OnAfterPublish(id, mutationSeq).ConfigureAwait(false);

            return true;
        }
        catch
        {
            // CRITICAL SAFETY GATE:
            // Transaction committed to PostgreSQL, but in-memory publication failed or was interrupted!
            // Fail closed by marking coordinator unhealthy so queries reject rather than serve stale data.
            IsHealthy = false;
            throw;
        }
    }

    public async Task<bool> InsertAccountAsync(long id, int balance, bool active, CancellationToken ct = default)
    {
        if (id <= 0 || (ulong)(id - 1) >= (ulong)_bitmap.MaxId)
            throw new ArgumentOutOfRangeException(nameof(id), $"Account ID {id} exceeds allocated capacity {_bitmap.MaxId}.");

        int shard = GetShard(id);
        var sem = _shardLocks[shard];

        await sem.WaitAsync(ct).ConfigureAwait(false);
        long mutationSeq = 0;
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            if (OnBeforeAcquireDbLock != null)
                await OnBeforeAcquireDbLock(id).ConfigureAwait(false);

            try
            {
                await using var advCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('eap_outbox_lock'));", conn, tx);
                await advCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch { }

            if (OnAfterAcquireDbLock != null)
                await OnAfterAcquireDbLock(id).ConfigureAwait(false);

            string insertSql = $@"
                INSERT INTO {_tableName} (id, balance, active, version) 
                VALUES (@id, @b, @a, 1)
                ON CONFLICT (id) DO UPDATE SET balance = @b, active = @a, version = {_tableName}.version + 1;";

            await using (var cmd = new NpgsqlCommand(insertSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("b", balance);
                cmd.Parameters.AddWithValue("a", active);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            try
            {
                string logSql = $@"
                    INSERT INTO {_mutationLogTable} (account_id, balance, active) 
                    VALUES (@id, @b, @a) 
                    RETURNING seq_id;";

                await using var logCmd = new NpgsqlCommand(logSql, conn, tx);
                logCmd.Parameters.AddWithValue("id", id);
                logCmd.Parameters.AddWithValue("b", balance);
                logCmd.Parameters.AddWithValue("a", active);

                object? seqVal = await logCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (seqVal != null && seqVal != DBNull.Value)
                {
                    mutationSeq = Convert.ToInt64(seqVal);
                    if (EnforceContiguousWatermark)
                    {
                        _watermarkTracker.RegisterInFlight(mutationSeq);
                    }
                }
            }
            catch { }

            if (OnBeforeCommit != null)
                await OnBeforeCommit(id).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            if (OnAfterCommit != null)
                await OnAfterCommit(id, mutationSeq).ConfigureAwait(false);
        }
        catch
        {
            if (EnforceContiguousWatermark && mutationSeq > 0)
            {
                _watermarkTracker.CancelInFlight(mutationSeq);
            }
            throw;
        }
        finally
        {
            sem.Release();
        }

        try
        {
            if (OnBeforePublish != null)
                await OnBeforePublish(id, mutationSeq).ConfigureAwait(false);

            PublishCrossPlane(id, balance, active, mutationSeq);

            if (OnAfterPublish != null)
                await OnAfterPublish(id, mutationSeq).ConfigureAwait(false);

            return true;
        }
        catch
        {
            IsHealthy = false;
            throw;
        }
    }

    public async Task<bool> DeleteAccountAsync(long id, CancellationToken ct = default)
    {
        if (id <= 0 || (ulong)(id - 1) >= (ulong)_bitmap.MaxId)
            throw new ArgumentOutOfRangeException(nameof(id), $"Account ID {id} exceeds allocated capacity {_bitmap.MaxId}.");

        int shard = GetShard(id);
        var sem = _shardLocks[shard];

        await sem.WaitAsync(ct).ConfigureAwait(false);
        long mutationSeq = 0;
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            if (OnBeforeAcquireDbLock != null)
                await OnBeforeAcquireDbLock(id).ConfigureAwait(false);

            try
            {
                await using var advCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('eap_outbox_lock'));", conn, tx);
                await advCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch { }

            if (OnAfterAcquireDbLock != null)
                await OnAfterAcquireDbLock(id).ConfigureAwait(false);

            string deleteSql = $"DELETE FROM {_tableName} WHERE id = @id;";
            await using (var cmd = new NpgsqlCommand(deleteSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", id);
                int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }
            }

            try
            {
                string logSql = $@"
                    INSERT INTO {_mutationLogTable} (account_id, balance, active) 
                    VALUES (@id, 0, false) 
                    RETURNING seq_id;";

                await using var logCmd = new NpgsqlCommand(logSql, conn, tx);
                logCmd.Parameters.AddWithValue("id", id);

                object? seqVal = await logCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (seqVal != null && seqVal != DBNull.Value)
                {
                    mutationSeq = Convert.ToInt64(seqVal);
                    if (EnforceContiguousWatermark)
                    {
                        _watermarkTracker.RegisterInFlight(mutationSeq);
                    }
                }
            }
            catch { }

            if (OnBeforeCommit != null)
                await OnBeforeCommit(id).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            if (OnAfterCommit != null)
                await OnAfterCommit(id, mutationSeq).ConfigureAwait(false);
        }
        catch
        {
            if (EnforceContiguousWatermark && mutationSeq > 0)
            {
                _watermarkTracker.CancelInFlight(mutationSeq);
            }
            throw;
        }
        finally
        {
            sem.Release();
        }

        try
        {
            if (OnBeforePublish != null)
                await OnBeforePublish(id, mutationSeq).ConfigureAwait(false);

            PublishCrossPlane(id, 0, false, mutationSeq);

            if (OnAfterPublish != null)
                await OnAfterPublish(id, mutationSeq).ConfigureAwait(false);

            return true;
        }
        catch
        {
            IsHealthy = false;
            throw;
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
        cmd.Parameters.AddWithValue("lastSeq", LastAppliedMutationSeq);

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

                if (EnforceContiguousWatermark)
                {
                    _watermarkTracker.RegisterInFlight(seq);
                }
                PublishCrossPlane(accountId, balance, active, seq);
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
            if (EnforceContiguousWatermark)
            {
                _watermarkTracker.Reset(snapshotWatermark);
            }
            else
            {
                Interlocked.Exchange(ref _legacyLastAppliedMutationSeq, snapshotWatermark);
            }
            _accountSequences.Clear();

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
