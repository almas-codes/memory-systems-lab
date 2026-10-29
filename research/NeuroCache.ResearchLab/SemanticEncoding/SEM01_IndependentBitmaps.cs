using System;
using System.Runtime.CompilerServices;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.SemanticEncoding;

/// <summary>
/// SEM-01: One Independent Bitmap per Predicate
/// Allocates one compact Boolean bitmap per declared query.
/// Footprint for 50M records: 8 x 6.25 MB = 50.00 MB decimal.
/// </summary>
public sealed class SEM01_IndependentBitmaps : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "SEM-BITMAPS-001";
    public string CandidateName => "Independent Bitmaps per Predicate (SEM-01)";
    public string Family => "SemanticEncoding";
    public string Description => "Stores 8 separate 1-bit boolean bitmaps (6.25 MB each).";

    private readonly EapBooleanBitmap[] _bitmaps;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _bitmaps.Length * _bitmaps[0].RawMemoryBytes;
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 64;

    public SEM01_IndependentBitmaps(long maxId)
    {
        _maxId = maxId;
        _bitmaps = new EapBooleanBitmap[8];
        for (int i = 0; i < 8; i++) _bitmaps[i] = new EapBooleanBitmap(maxId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex) => _bitmaps[predicateIndex].IsEligible(id);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        var r = new AccountRecord(id, balance, active, sequenceId);
        for (int i = 0; i < 8; i++)
        {
            _bitmaps[i].SetEligible(id, r.EvaluatePredicate(i));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        int p = 0;
        for (int i = 0; i < 8; i++)
        {
            if (_bitmaps[i].IsEligible(id)) p |= (1 << (7 - i));
        }
        return (byte)p;
    }
}
