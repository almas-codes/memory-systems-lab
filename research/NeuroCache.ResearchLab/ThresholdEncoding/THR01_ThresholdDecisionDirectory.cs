using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.ThresholdEncoding;

/// <summary>
/// THR-01: Exact Threshold Decision Directory
/// Partitions numeric balance into 6 intervals defined by 5 operational thresholds (100, 250, 500, 750, 900).
/// Stores interval ID (0..5) in a compact 3-bit array. Resolves declared threshold queries in O(1).
/// </summary>
public sealed class THR01_ThresholdDecisionDirectory : IExperimentCandidate, IExactRecordLookup<long, int>, IMemoryAccountable
{
    public string ExperimentId => "THR-DIR-001";
    public string CandidateName => "Exact Threshold Decision Directory (THR-01)";
    public string Family => "ThresholdEncoding";
    public string Description => "Partitions balance domain into 6 intervals, storing 3-bit interval IDs per account.";

    public static readonly int[] Thresholds = new[] { 100, 250, 500, 750, 900 };

    private readonly byte[] _intervals; // 1 byte per record (or packed 3-bit)
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _intervals.LongLength;
    public long PayloadBytes => _intervals.LongLength;
    public long MetadataBytes => 64;

    public THR01_ThresholdDecisionDirectory(long maxId)
    {
        _maxId = maxId;
        _intervals = new byte[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ComputeInterval(int balance)
    {
        if (balance < 100) return 0;
        if (balance < 250) return 1;
        if (balance < 500) return 2;
        if (balance < 750) return 3;
        if (balance < 900) return 4;
        return 5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBalance(long id, int balance)
    {
        _intervals[id] = ComputeInterval(balance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Query(long id) => _intervals[id];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluateThreshold(long id, int threshold)
    {
        byte interval = _intervals[id];
        return threshold switch
        {
            100 => interval >= 1,
            250 => interval >= 2,
            500 => interval >= 3,
            750 => interval >= 4,
            900 => interval >= 5,
            _ => throw new NotSupportedException($"Undeclared threshold {threshold}; directory is exact for declared thresholds only.")
        };
    }
}
