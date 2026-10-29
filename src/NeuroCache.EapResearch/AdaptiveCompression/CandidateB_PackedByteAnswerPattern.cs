using System;
using System.Runtime.CompilerServices;

namespace NeuroCache.EapResearch.AdaptiveCompression;

/// <summary>
/// Candidate B: Packed 8-bit Byte Answer Pattern.
/// Stores the complete 8-query answer vector as a single contiguous byte per record.
/// Footprint for 50,000,000 records: 50,000,000 bytes = 50.00 MB decimal (47.68 MiB binary).
/// </summary>
public sealed class CandidateB_PackedByteAnswerPattern
{
    private readonly byte[] _patterns;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _patterns.LongLength;

    public CandidateB_PackedByteAnswerPattern(long maxId)
    {
        _maxId = maxId;
        _patterns = new byte[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool QuerySingle(long id, int queryIndex)
    {
        byte pat = _patterns[id];
        return ((pat >> (7 - queryIndex)) & 1) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        return _patterns[id];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(long id, int balance, bool active)
    {
        _patterns[id] = QueryFamily.ComputeAnswerPattern(balance, active);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetExplicitPattern(long id, byte pattern)
    {
        _patterns[id] = pattern;
    }
}
