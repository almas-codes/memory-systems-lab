using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.Representations;

/// <summary>
/// REP-03: Minimal Sufficient Statistic Vector (MSSV)
/// Identifies and stores the minimal sufficient statistic (1-bit active flag + 3-bit balance interval tier)
/// packed into 4 bits per record (2 records per byte).
/// </summary>
public sealed class REP03_MinimalSufficientStatisticVector : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "REP-MSSV-003";
    public string CandidateName => "Minimal Sufficient Statistic Vector (MSSV)";
    public string Family => "Representations";
    public string Description => "Stores 4-bit sufficient statistic (1-bit active + 3-bit interval) answering all 8 queries exactly.";

    private readonly byte[] _packedData;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _packedData.LongLength;
    public long PayloadBytes => _packedData.LongLength;
    public long MetadataBytes => 64;

    public REP03_MinimalSufficientStatisticVector(long maxId)
    {
        _maxId = maxId;
        _packedData = new byte[(maxId + 2) / 2];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ComputeStatistic(int balance, bool active)
    {
        // Bits 0..2: Tier (0..5)
        // Bit 3: Active
        int tier;
        if (balance < 100) tier = 0;
        else if (balance < 250) tier = 1;
        else if (balance < 500) tier = 2;
        else if (balance < 750) tier = 3;
        else if (balance < 900) tier = 4;
        else tier = 5;

        int stat = tier;
        if (active) stat |= (1 << 3);
        return (byte)stat;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetStatistic(long id)
    {
        long byteIdx = id >> 1;
        byte b = _packedData[byteIdx];
        return (id & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStatistic(long id, byte stat)
    {
        long byteIdx = id >> 1;
        bool isHigh = (id & 1) == 0;
        ref byte target = ref _packedData[byteIdx];
        byte cur, next;
        do
        {
            cur = Volatile.Read(ref target);
            if (isHigh)
                next = (byte)((cur & 0x0F) | (stat << 4));
            else
                next = (byte)((cur & 0xF0) | (stat & 0x0F));
        } while (Interlocked.CompareExchange(ref target, next, cur) != cur);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        byte stat = GetStatistic(id);
        bool active = (stat & (1 << 3)) != 0;
        int tier = stat & 0x07;

        return predicateIndex switch
        {
            0 => active,
            1 => active && tier >= 1, // bal >= 100
            2 => active && tier >= 2, // bal >= 250
            3 => active && tier >= 3, // bal >= 500
            4 => active && tier >= 4, // bal >= 750
            5 => active && tier >= 5, // bal >= 900
            6 => tier >= 3,           // bal >= 500
            7 => tier >= 1,           // bal >= 100
            _ => false
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        byte stat = ComputeStatistic(balance, active);
        SetStatistic(id, stat);
    }
}
