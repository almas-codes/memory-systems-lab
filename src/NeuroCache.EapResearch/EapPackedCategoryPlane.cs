using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch;

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
    public bool EvaluateThreshold(long id, int threshold)
    {
        if ((ulong)(id - 1) >= (ulong)_maxId)
            return false;

        long ordinal = id - 1;

        // 1. Check active bit
        int activeWord = (int)(ordinal >> 6);
        int activeBit = (int)(ordinal & 63);
        if ((_activeBits[activeWord] & (1UL << activeBit)) == 0)
            return false;

        // 2. Check 4-bit category
        int byteIndex = (int)(ordinal >> 1);
        byte b = _categories[byteIndex];
        byte cat = (ordinal & 1) == 0 ? (byte)(b & 0x0F) : (byte)(b >> 4);

        byte reqCat = ThresholdToCategory(threshold);
        return cat >= reqCat;
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
}
