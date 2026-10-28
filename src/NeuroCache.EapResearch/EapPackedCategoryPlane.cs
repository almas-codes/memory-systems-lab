using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch;

public enum CategoryEvaluationResult
{
    DefiniteFalse = 0,
    DefiniteTrue = 1,
    AmbiguousBucket = 2
}

public sealed class EapPackedCategoryPlane
{
    private readonly byte[] _categories;
    private readonly ulong[] _activeBits;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long RawMemoryBytes => _categories.LongLength + ((long)_activeBits.Length * sizeof(ulong));

    public EapPackedCategoryPlane(long maxId)
    {
        _maxId = maxId;
        _categories = new byte[(maxId + 1) / 2];
        _activeBits = new ulong[(maxId + 63) / 64];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte BalanceToCategory(int balance)
    {
        if (balance <= 0) return 0;
        int cat = balance / 13;
        return (byte)Math.Clamp(cat, 0, 15);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ThresholdToCategory(int threshold)
    {
        return BalanceToCategory(threshold);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsThresholdExact(int threshold)
    {
        return threshold >= 0 && threshold <= 195 && (threshold % 13 == 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CategoryEvaluationResult EvaluateThresholdDetailed(long id, int threshold)
    {
        if ((ulong)(id - 1) >= (ulong)_maxId)
            return CategoryEvaluationResult.DefiniteFalse;

        long ordinal = id - 1;

        // 1. Check active bit
        int activeWord = (int)(ordinal >> 6);
        int activeBit = (int)(ordinal & 63);
        if ((_activeBits[activeWord] & (1UL << activeBit)) == 0)
            return CategoryEvaluationResult.DefiniteFalse;

        // 2. Check 4-bit category
        int byteIndex = (int)(ordinal >> 1);
        byte b = _categories[byteIndex];
        byte cat = (ordinal & 1) == 0 ? (byte)(b & 0x0F) : (byte)(b >> 4);

        if (threshold <= 0)
            return CategoryEvaluationResult.DefiniteTrue;
        if (threshold > 195)
        {
            // Cat 15 represents balance >= 195. Higher thresholds are ambiguous if cat == 15
            return cat == 15 ? CategoryEvaluationResult.AmbiguousBucket : CategoryEvaluationResult.DefiniteFalse;
        }

        int targetBucket = threshold / 13;
        int remainder = threshold % 13;

        if (cat > targetBucket)
            return CategoryEvaluationResult.DefiniteTrue;
        if (cat < targetBucket)
            return CategoryEvaluationResult.DefiniteFalse;

        // cat == targetBucket:
        // If remainder is 0, the threshold aligns exactly with the bucket floor.
        // Every account in this bucket has balance >= targetBucket * 13 == threshold.
        if (remainder == 0)
            return CategoryEvaluationResult.DefiniteTrue;

        // When remainder != 0, the threshold is inside the bucket: ambiguous.
        return CategoryEvaluationResult.AmbiguousBucket;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluateThreshold(long id, int threshold)
    {
        var result = EvaluateThresholdDetailed(id, threshold);
        // Fast in-memory evaluation: returns true only if definitely satisfied
        return result == CategoryEvaluationResult.DefiniteTrue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAccount(long id, int balance, bool active)
    {
        if ((ulong)(id - 1) >= (ulong)_maxId)
            return;

        long ordinal = id - 1;

        // Update active bit
        int activeWord = (int)(ordinal >> 6);
        int activeBit = (int)(ordinal & 63);
        ulong mask = 1UL << activeBit;
        if (active)
            Interlocked.Or(ref _activeBits[activeWord], mask);
        else
            Interlocked.And(ref _activeBits[activeWord], ~mask);

        // Update category nibble
        int byteIndex = (int)(ordinal >> 1);
        byte cat = BalanceToCategory(balance);

        lock (_categories)
        {
            byte cur = _categories[byteIndex];
            if ((ordinal & 1) == 0)
                _categories[byteIndex] = (byte)((cur & 0xF0) | (cat & 0x0F));
            else
                _categories[byteIndex] = (byte)((cur & 0x0F) | (cat << 4));
        }
    }

    public async Task<TimeSpan> BuildFromPostgreSqlAsync(
        string connectionString,
        string tableName = "eap_lab.accounts_500m",
        Action<long, long>? progressCallback = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Fast sequential streaming read
        string query = $"SELECT id, balance, active FROM {tableName};";
        await using var cmd = new NpgsqlCommand(query, conn);
        cmd.CommandTimeout = 1800;

        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);

        long processed = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            int balance = reader.GetInt32(1);
            bool active = reader.GetBoolean(2);

            if ((ulong)(id - 1) < (ulong)_maxId)
            {
                long ordinal = id - 1;
                if (active)
                {
                    int activeWord = (int)(ordinal >> 6);
                    int activeBit = (int)(ordinal & 63);
                    _activeBits[activeWord] |= (1UL << activeBit);
                }

                int byteIndex = (int)(ordinal >> 1);
                byte cat = BalanceToCategory(balance);
                if ((ordinal & 1) == 0)
                    _categories[byteIndex] = (byte)((_categories[byteIndex] & 0xF0) | (cat & 0x0F));
                else
                    _categories[byteIndex] = (byte)((_categories[byteIndex] & 0x0F) | (cat << 4));
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
