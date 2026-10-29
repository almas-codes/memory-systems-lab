using System;
using System.Runtime.CompilerServices;

namespace NeuroCache.EapResearch.AdaptiveCompression;

/// <summary>
/// Candidate A: 8 Independent Boolean Bitmaps.
/// Represents each query as an isolated bit-plane.
/// Footprint for 50,000,000 records: 8 x 6.25 MB = 50.00 MB decimal (47.68 MiB binary).
/// </summary>
public sealed class CandidateA_EightIndependentBitmaps
{
    private readonly EapBooleanBitmap[] _bitmaps;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _bitmaps.Length * _bitmaps[0].RawMemoryBytes;

    public CandidateA_EightIndependentBitmaps(long maxId)
    {
        _maxId = maxId;
        _bitmaps = new EapBooleanBitmap[QueryFamily.QueryCount];
        for (int i = 0; i < QueryFamily.QueryCount; i++)
        {
            _bitmaps[i] = new EapBooleanBitmap(maxId);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool QuerySingle(long id, int queryIndex)
    {
        return _bitmaps[queryIndex].IsEligible(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        int pattern = 0;
        for (int i = 0; i < QueryFamily.QueryCount; i++)
        {
            if (_bitmaps[i].IsEligible(id))
            {
                pattern |= (1 << (7 - i));
            }
        }
        return (byte)pattern;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(long id, int balance, bool active)
    {
        for (int i = 0; i < QueryFamily.QueryCount; i++)
        {
            bool ans = QueryFamily.EvaluatePredicate(i, balance, active);
            _bitmaps[i].SetEligible(id, ans);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetExplicitPattern(long id, byte pattern)
    {
        for (int i = 0; i < QueryFamily.QueryCount; i++)
        {
            bool ans = QueryFamily.ExtractAnswer(pattern, i);
            _bitmaps[i].SetEligible(id, ans);
        }
    }
}
