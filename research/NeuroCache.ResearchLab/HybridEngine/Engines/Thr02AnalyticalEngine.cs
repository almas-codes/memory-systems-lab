using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.ThresholdEncoding;

namespace NeuroCache.ResearchLab.HybridEngine.Engines;

public sealed class Thr02AnalyticalEngine : IAnalyticalQueryEngine
{
    private THR02_BitSlicedNumericEvaluation _bitSliced;
    private ulong[] _activeBits;
    private long _appliedVersion;
    private readonly long _maxId;
    private readonly int _wordCount;
    private readonly object _writeLock = new();

    public string EngineName => "THR-02 (Bit-Sliced Numeric Engine)";
    public long AppliedVersion => Volatile.Read(ref _appliedVersion);
    public long MaxSupportedId => _maxId;
    public long TotalAllocatedBytes => _bitSliced.TotalAllocatedBytes + (long)_activeBits.Length * sizeof(ulong);

    public Thr02AnalyticalEngine(long maxId, long initialVersion = 0)
    {
        _maxId = maxId;
        _wordCount = (int)((maxId + 63) / 64);
        _bitSliced = new THR02_BitSlicedNumericEvaluation(maxId);
        _activeBits = new ulong[_wordCount];
        _appliedVersion = initialVersion;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SupportsAnalytical(AnalyticalQuery query)
    {
        if (query == null) return false;
        if (query.Threshold < 0 || query.Threshold >= 1024) return false;
        if (query.BatchThresholds != null)
        {
            foreach (int t in query.BatchThresholds)
            {
                if (t < 0 || t >= 1024) return false;
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long CountMatching(AnalyticalQuery query, long maxAccountId)
    {
        long targetMax = maxAccountId > 0 && maxAccountId <= _maxId ? maxAccountId : _maxId;
        int targetWords = (int)((targetMax + 63) / 64);
        int remainder = (int)(targetMax & 63);

        long count = 0;
        int threshold = query.Threshold;
        bool reqActive = query.RequireActive;

        for (int w = 0; w < targetWords; w++)
        {
            ulong mask = _bitSliced.EvaluateThresholdWord(w, threshold);
            if (reqActive)
            {
                mask &= Volatile.Read(ref _activeBits[w]);
            }

            // Mask tail of last word if not full
            if (w == targetWords - 1 && remainder != 0)
            {
                ulong validBitsMask = (1UL << remainder) - 1UL;
                mask &= validBitsMask;
            }

            count += BitOperations.PopCount(mask);
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public List<long> GetMatchingIds(AnalyticalQuery query, long maxAccountId, int limit)
    {
        var result = new List<long>(Math.Min(limit, 1024));
        long targetMax = maxAccountId > 0 && maxAccountId <= _maxId ? maxAccountId : _maxId;
        int targetWords = (int)((targetMax + 63) / 64);
        int remainder = (int)(targetMax & 63);

        int threshold = query.Threshold;
        bool reqActive = query.RequireActive;

        for (int w = 0; w < targetWords && result.Count < limit; w++)
        {
            ulong mask = _bitSliced.EvaluateThresholdWord(w, threshold);
            if (reqActive)
            {
                mask &= Volatile.Read(ref _activeBits[w]);
            }

            if (w == targetWords - 1 && remainder != 0)
            {
                ulong validBitsMask = (1UL << remainder) - 1UL;
                mask &= validBitsMask;
            }

            while (mask != 0 && result.Count < limit)
            {
                int bit = BitOperations.TrailingZeroCount(mask);
                long id = ((long)w << 6) + bit + 1;
                result.Add(id);
                mask &= mask - 1UL; // Clear lowest set bit
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long[] EvaluateThresholdBatch(int[] thresholds, bool requireActive, long maxAccountId)
    {
        long[] counts = new long[thresholds.Length];
        long targetMax = maxAccountId > 0 && maxAccountId <= _maxId ? maxAccountId : _maxId;
        int targetWords = (int)((targetMax + 63) / 64);
        int remainder = (int)(targetMax & 63);

        for (int w = 0; w < targetWords; w++)
        {
            ulong act = requireActive ? Volatile.Read(ref _activeBits[w]) : ~0UL;
            if (w == targetWords - 1 && remainder != 0)
            {
                act &= ((1UL << remainder) - 1UL);
            }

            for (int t = 0; t < thresholds.Length; t++)
            {
                ulong m = _bitSliced.EvaluateThresholdWord(w, thresholds[t]) & act;
                counts[t] += BitOperations.PopCount(m);
            }
        }

        return counts;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyMutation(long accountId, int balance, bool active, long commitSeq)
    {
        if (accountId < 1 || accountId > _maxId) return;

        lock (_writeLock)
        {
            long curVer = Volatile.Read(ref _appliedVersion);
            if (commitSeq < curVer && curVer > 0)
            {
                // Stale event arrived out-of-order; drop to maintain causal consistency
                return;
            }

            _bitSliced.SetBalance(accountId, balance);

            long zero = accountId - 1;
            int word = (int)(zero >> 6);
            int bit = (int)(zero & 63);
            ulong mask = 1UL << bit;

            ref ulong activeWord = ref _activeBits[word];
            ulong cur = Volatile.Read(ref activeWord);
            ulong next = active ? (cur | mask) : (cur & ~mask);
            Volatile.Write(ref activeWord, next);

            if (commitSeq > curVer)
            {
                Volatile.Write(ref _appliedVersion, commitSeq);
            }
        }
    }

    public void Rebuild(long maxId, IEnumerable<(long id, int balance, bool active)> records, long snapshotVersion)
    {
        lock (_writeLock)
        {
            var nextBitSliced = new THR02_BitSlicedNumericEvaluation(maxId);
            int wordCount = (int)((maxId + 63) / 64);
            var nextActiveBits = new ulong[wordCount];

            foreach (var (id, bal, act) in records)
            {
                if (id >= 1 && id <= maxId)
                {
                    nextBitSliced.SetBalance(id, bal);
                    if (act)
                    {
                        long zero = id - 1;
                        int word = (int)(zero >> 6);
                        int bit = (int)(zero & 63);
                        nextActiveBits[word] |= (1UL << bit);
                    }
                }
            }

            Interlocked.Exchange(ref _bitSliced, nextBitSliced);
            Interlocked.Exchange(ref _activeBits, nextActiveBits);
            Volatile.Write(ref _appliedVersion, snapshotVersion);
        }
    }
}
