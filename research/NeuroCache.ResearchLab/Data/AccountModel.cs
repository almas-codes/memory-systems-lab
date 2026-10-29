using System;
using System.Runtime.CompilerServices;

namespace NeuroCache.ResearchLab.Data;

public readonly record struct AccountRecord(long Id, int Balance, bool Active, long Version)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(int index) => index switch
    {
        0 => Active,
        1 => Active && Balance >= 100,
        2 => Active && Balance >= 250,
        3 => Active && Balance >= 500,
        4 => Active && Balance >= 750,
        5 => Active && Balance >= 900,
        6 => Balance >= 500,
        7 => Balance >= 100,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ComputeAnswerPattern()
    {
        int p = 0;
        if (Active)
        {
            p |= (1 << 7);
            if (Balance >= 100) p |= (1 << 6);
            if (Balance >= 250) p |= (1 << 5);
            if (Balance >= 500) p |= (1 << 4);
            if (Balance >= 750) p |= (1 << 3);
            if (Balance >= 900) p |= (1 << 2);
        }
        if (Balance >= 500) p |= (1 << 1);
        if (Balance >= 100) p |= (1 << 0);
        return (byte)p;
    }
}
