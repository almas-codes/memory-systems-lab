using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.ThresholdEncoding;

/// <summary>
/// THR-02: Bit-Sliced Exact Numeric Evaluation
/// Encodes numeric balances across 10 bit-planes. Uses word-wise boolean arithmetic for batch comparisons.
/// </summary>
public sealed class THR02_BitSlicedNumericEvaluation : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "THR-BITSLICE-002";
    public string CandidateName => "Bit-Sliced Exact Numeric Evaluation (THR-02)";
    public string Family => "ThresholdEncoding";
    public string Description => "Encodes 10-bit integer balances across 10 vertical bit-planes; evaluates word-wise comparisons.";

    public const int BitPlanes = 10; // Supports balances up to 1023
    private readonly ulong[][] _planes; // 10 planes of (maxId + 63)/64 words
    private readonly long _maxId;
    private readonly int _wordCount;

    public long MaxId => _maxId;
    public int WordCount => _wordCount;
    public long TotalAllocatedBytes => _planes.Length * (long)_wordCount * sizeof(ulong);
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 128;

    public THR02_BitSlicedNumericEvaluation(long maxId)
    {
        _maxId = maxId;
        _wordCount = (int)((maxId + 63) / 64);
        _planes = new ulong[BitPlanes][];
        for (int p = 0; p < BitPlanes; p++)
        {
            _planes[p] = new ulong[_wordCount];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBalance(long id, int balance)
    {
        long zero = id - 1;
        int word = (int)(zero >> 6);
        int bit = (int)(zero & 63);
        ulong mask = 1UL << bit;

        for (int p = 0; p < BitPlanes; p++)
        {
            bool bitSet = ((balance >> p) & 1) != 0;
            if (bitSet) _planes[p][word] |= mask;
            else _planes[p][word] &= ~mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBalance(long id)
    {
        long zero = id - 1;
        int word = (int)(zero >> 6);
        int bit = (int)(zero & 63);

        int val = 0;
        for (int p = 0; p < BitPlanes; p++)
        {
            if ((_planes[p][word] & (1UL << bit)) != 0)
            {
                val |= (1 << p);
            }
        }
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluateThresholdSingle(long id, int threshold)
    {
        return ReadBalance(id) >= threshold;
    }

    /// <summary>
    /// Evaluates 64 accounts simultaneously for (balance >= threshold) using bit-sliced bitwise arithmetic.
    /// Bit-sliced Greater-Than-or-Equal algorithm:
    /// GT = 0, EQ = ~0
    /// For bit p from 9 down to 0:
    ///   thresholdBit = (threshold >> p) & 1
    ///   if thresholdBit == 1:
    ///     EQ &= plane[p]
    ///   else:
    ///     GT |= EQ & plane[p]
    /// Result = GT | EQ
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong EvaluateThresholdWord(int wordIndex, int threshold)
    {
        ulong gt = 0UL;
        ulong eq = ~0UL;

        for (int p = BitPlanes - 1; p >= 0; p--)
        {
            ulong plane = _planes[p][wordIndex];
            int tBit = (threshold >> p) & 1;

            if (tBit == 1)
            {
                eq &= plane;
            }
            else
            {
                gt |= eq & plane;
            }
        }

        return gt | eq;
    }
}
