using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch;

public sealed class EapCoordinator : IAsyncDisposable
{
    private const int ShardCount = 1024;
    private readonly SemaphoreSlim[] _shardLocks = new SemaphoreSlim[ShardCount];
    private readonly string _connectionString;
    private readonly EapBooleanBitmap _bitmap;
    private readonly EapPackedCategoryPlane _categoryPlane;
    private readonly int _defaultThreshold;

    public EapBooleanBitmap Bitmap => _bitmap;
    public EapPackedCategoryPlane CategoryPlane => _categoryPlane;

    public EapCoordinator(long maxId, string connectionString, int defaultThreshold = 100)
    {
        _connectionString = connectionString;
        _defaultThreshold = defaultThreshold;
        _bitmap = new EapBooleanBitmap(maxId);
        _categoryPlane = new EapPackedCategoryPlane(maxId);

        for (int i = 0; i < ShardCount; i++)
        {
            _shardLocks[i] = new SemaphoreSlim(1, 1);
        }
    }

    private int GetShard(long id) => (int)((ulong)id % (ulong)ShardCount);

    public bool QueryEligible(long id)
    {
        // Lock-free atomic read from word
        return _bitmap.IsEligible(id);
    }

    public bool QueryThreshold(long id, int threshold)
    {
        return _categoryPlane.EvaluateThreshold(id, threshold);
    }

    public async Task<bool> UpdateAccountAsync(long id, int newBalance, bool newActive, CancellationToken ct = default)
    {
        int shard = GetShard(id);
        var sem = _shardLocks[shard];

        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Update authoritative PostgreSQL source
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE eap_lab.accounts_500m 
                SET balance = @b, active = @a, version = version + 1 
                WHERE id = @id;", conn, tx))
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

            await tx.CommitAsync(ct).ConfigureAwait(false);

            // Update derived answer plane atomically
            bool newEligible = (newBalance >= _defaultThreshold) && newActive;
            _bitmap.SetEligible(id, newEligible);
            _categoryPlane.SetAccount(id, newBalance, newActive);

            return true;
        }
        finally
        {
            sem.Release();
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
