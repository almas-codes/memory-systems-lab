using System;
using System.Runtime.CompilerServices;

namespace NeuroCache.EapResearch.AdaptiveCompression;

/// <summary>
/// Defines the 8 deterministic queries comprising the exact query family for NeuroCache.
/// Schema: id BIGINT, balance INT, active BOOLEAN, version BIGINT.
/// </summary>
public static class QueryFamily
{
    public const int QueryCount = 8;

    public static readonly string[] QueryNames = new[]
    {
        "Q1_Active",
        "Q2_ActiveAndBalGte100",
        "Q3_ActiveAndBalGte250",
        "Q4_ActiveAndBalGte500",
        "Q5_ActiveAndBalGte750",
        "Q6_ActiveAndBalGte900",
        "Q7_BalGte500",
        "Q8_BalGte100"
    };

    public static readonly string[] QuerySqlPredicates = new[]
    {
        "active",
        "(balance >= 100 AND active)",
        "(balance >= 250 AND active)",
        "(balance >= 500 AND active)",
        "(balance >= 750 AND active)",
        "(balance >= 900 AND active)",
        "(balance >= 500)",
        "(balance >= 100)"
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool EvaluatePredicate(int queryIndex, int balance, bool active)
    {
        return queryIndex switch
        {
            0 => active,
            1 => active && balance >= 100,
            2 => active && balance >= 250,
            3 => active && balance >= 500,
            4 => active && balance >= 750,
            5 => active && balance >= 900,
            6 => balance >= 500,
            7 => balance >= 100,
            _ => throw new ArgumentOutOfRangeException(nameof(queryIndex), "Query index must be 0..7")
        };
    }

    /// <summary>
    /// Computes the exact 8-bit answer pattern for a record:
    /// Bit 7: Q1_Active
    /// Bit 6: Q2_ActiveAndBalGte100
    /// Bit 5: Q3_ActiveAndBalGte250
    /// Bit 4: Q4_ActiveAndBalGte500
    /// Bit 3: Q5_ActiveAndBalGte750
    /// Bit 2: Q6_ActiveAndBalGte900
    /// Bit 1: Q7_BalGte500
    /// Bit 0: Q8_BalGte100
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ComputeAnswerPattern(int balance, bool active)
    {
        int p = 0;
        if (active)
        {
            p |= (1 << 7); // Q1
            if (balance >= 100) p |= (1 << 6); // Q2
            if (balance >= 250) p |= (1 << 5); // Q3
            if (balance >= 500) p |= (1 << 4); // Q4
            if (balance >= 750) p |= (1 << 3); // Q5
            if (balance >= 900) p |= (1 << 2); // Q6
        }
        if (balance >= 500) p |= (1 << 1); // Q7
        if (balance >= 100) p |= (1 << 0); // Q8
        return (byte)p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ExtractAnswer(byte answerPattern, int queryIndex)
    {
        int shift = 7 - queryIndex;
        return ((answerPattern >> shift) & 1) != 0;
    }
}
