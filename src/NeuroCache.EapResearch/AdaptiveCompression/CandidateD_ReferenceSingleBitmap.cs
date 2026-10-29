using System.Runtime.CompilerServices;

namespace NeuroCache.EapResearch.AdaptiveCompression;

/// <summary>
/// Candidate D: Reference Single Boolean Bitmap.
/// Stores only 1 exact Boolean answer per record (e.g., Q1_Active).
/// Footprint for 50,000,000 records: 6.25 MB decimal (5.96 MiB binary).
/// Used as baseline reference for single-answer overhead.
/// </summary>
public sealed class CandidateD_ReferenceSingleBitmap
{
    private readonly EapBooleanBitmap _bitmap;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _bitmap.RawMemoryBytes;

    public CandidateD_ReferenceSingleBitmap(long maxId)
    {
        _maxId = maxId;
        _bitmap = new EapBooleanBitmap(maxId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool QuerySingle(long id)
    {
        return _bitmap.IsEligible(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(long id, bool active)
    {
        _bitmap.SetEligible(id, active);
    }
}
