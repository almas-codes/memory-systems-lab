using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.AlternativeEncodings;

/// <summary>
/// ALT-05: Exact Sufficient-Fact Columns
/// Stores only the minimal sufficient facts (Balance INT + Active BIT) as contiguous columnar arrays, discarding unneeded columns.
/// </summary>
public sealed class ALT05_ExactSufficientFactColumns : IExperimentCandidate, IPredicateEvaluator<long>, IMemoryAccountable
{
    public string ExperimentId => "ALT-FACTS-005";
    public string CandidateName => "Exact Sufficient-Fact Columns (ALT-05)";
    public string Family => "AlternativeEncodings";
    public string Description => "Stores contiguous columnar arrays of Balance (32-bit) and Active (1-bit), discarding unreferenced relational fields.";

    private readonly int[] _balances;
    private readonly ulong[] _activeBits;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => (_balances.LongLength * 4L) + (_activeBits.LongLength * 8L);
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 64;

    public ALT05_ExactSufficientFactColumns(long maxId)
    {
        _maxId = maxId;
        _balances = new int[maxId + 1];
        _activeBits = new ulong[(maxId + 63) / 64];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetRecord(long id, int balance, bool active)
    {
        _balances[id] = balance;
        long zero = id - 1;
        if (active) _activeBits[zero >> 6] |= (1UL << (int)(zero & 63));
        else _activeBits[zero >> 6] &= ~(1UL << (int)(zero & 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        int bal = _balances[id];
        long zero = id - 1;
        bool active = (_activeBits[zero >> 6] & (1UL << (int)(zero & 63))) != 0;

        return predicateIndex switch
        {
            0 => active,
            1 => active && bal >= 100,
            2 => active && bal >= 250,
            3 => active && bal >= 500,
            4 => active && bal >= 750,
            5 => active && bal >= 900,
            6 => bal >= 500,
            7 => bal >= 100,
            _ => false
        };
    }
}
