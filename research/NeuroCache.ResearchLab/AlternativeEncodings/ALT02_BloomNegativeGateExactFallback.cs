using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.AlternativeEncodings;

/// <summary>
/// ALT-02: Bloom/Quotient Negative Gate with Exact Fallback (BQNC)
/// High-speed probabilistic negative gate: filters definite negatives instantly; possible positives check exact oracle.
/// Never returns false positives or false negatives.
/// </summary>
public sealed class ALT02_BloomNegativeGateExactFallback : IExperimentCandidate, IExactRecordLookup<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "ALT-BLOOM-002";
    public string CandidateName => "Bloom Negative Gate with Exact Fallback (BQNC / ALT-02)";
    public string Family => "AlternativeEncodings";
    public string Description => "Filters definite negatives via 3-hash Bloom filter; verifies potential positives against exact set.";

    private readonly ulong[] _bloomFilter;
    private readonly HashSet<long> _exactPositiveSet = new();
    private readonly int _bitMask;

    public long TotalAllocatedBytes => (_bloomFilter.Length * 8L) + (_exactPositiveSet.Count * 24L);
    public long PayloadBytes => (_bloomFilter.Length * 8L) + (_exactPositiveSet.Count * 8L);
    public long MetadataBytes => _exactPositiveSet.Count * 16L;

    public ALT02_BloomNegativeGateExactFallback(int filterBits = 65536)
    {
        int words = (filterBits + 63) / 64;
        _bloomFilter = new ulong[words];
        _bitMask = (words * 64) - 1;
    }

    public void InsertPositive(long id)
    {
        _exactPositiveSet.Add(id);
        int h1 = Hash1(id);
        int h2 = Hash2(id);
        int h3 = Hash3(id);

        _bloomFilter[h1 >> 6] |= (1UL << (h1 & 63));
        _bloomFilter[h2 >> 6] |= (1UL << (h2 & 63));
        _bloomFilter[h3 >> 6] |= (1UL << (h3 & 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        // 1. Fast Bloom check
        int h1 = Hash1(id);
        int h2 = Hash2(id);
        int h3 = Hash3(id);

        if ((_bloomFilter[h1 >> 6] & (1UL << (h1 & 63))) == 0) return false;
        if ((_bloomFilter[h2 >> 6] & (1UL << (h2 & 63))) == 0) return false;
        if ((_bloomFilter[h3 >> 6] & (1UL << (h3 & 63))) == 0) return false;

        // 2. Exact fallback verification (guarantees zero false positives!)
        return _exactPositiveSet.Contains(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Hash1(long id) => (int)((ulong)id * 0x517cc1b727220a95UL) & _bitMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Hash2(long id) => (int)((ulong)id * 0x6c62272e07bb0142UL) & _bitMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Hash3(long id) => (int)((ulong)id * 0xbf58476d1ce4e5b9UL) & _bitMask;
}
