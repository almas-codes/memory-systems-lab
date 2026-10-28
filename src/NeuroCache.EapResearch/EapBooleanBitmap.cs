using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch;

public sealed class EapBooleanBitmap
{
    private readonly ulong[] _bits;
    private readonly long _maxId;
    public long MaxId => _maxId;

    public long RawMemoryBytes => (long)_bits.Length * sizeof(ulong);

    public EapBooleanBitmap(long maxId)
    {
        _maxId = maxId;
        long wordCount = (maxId + 63) / 64;
        _bits = new ulong[wordCount];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEligible(long id)
    {
        if ((ulong)(id - 1) >= (ulong)_maxId)
            return false;

        long ordinal = id - 1;
        int word = (int)(ordinal >> 6);
        int bit = (int)(ordinal & 63);

        return (_bits[word] & (1UL << bit)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetEligible(long id, bool eligible)
    {
        if ((ulong)(id - 1) >= (ulong)_maxId)
            return;

        long ordinal = id - 1;
        int word = (int)(ordinal >> 6);
        int bit = (int)(ordinal & 63);
        ulong mask = 1UL << bit;

        if (eligible)
        {
            Interlocked.Or(ref _bits[word], mask);
        }
        else
        {
            Interlocked.And(ref _bits[word], ~mask);
        }
    }

    public async Task<TimeSpan> BuildFromPostgreSqlAsync(
        string connectionString,
        int threshold = 100,
        string tableName = "eap_lab.accounts_500m",
        Action<long, long>? progressCallback = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Stream accounts using sequential unbuffered reader without sorting
        string query = $"SELECT id, balance, active FROM {tableName};";
        await using var cmd = new NpgsqlCommand(query, conn);
        cmd.CommandTimeout = 1800; // 30 minutes

        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);

        long processed = 0;
        while (await reader.ReadAsync(ct))
        {
            long id = reader.GetInt64(0);
            int balance = reader.GetInt32(1);
            bool active = reader.GetBoolean(2);

            if ((ulong)(id - 1) < (ulong)_maxId)
            {
                bool eligible = (balance >= threshold) && active;
                if (eligible)
                {
                    long ordinal = id - 1;
                    int word = (int)(ordinal >> 6);
                    int bit = (int)(ordinal & 63);
                    _bits[word] |= (1UL << bit);
                }
            }

            processed++;
            if (progressCallback != null && processed % 50_000_000 == 0)
            {
                progressCallback(processed, _maxId);
            }
        }

        sw.Stop();
        return sw.Elapsed;
    }
}
