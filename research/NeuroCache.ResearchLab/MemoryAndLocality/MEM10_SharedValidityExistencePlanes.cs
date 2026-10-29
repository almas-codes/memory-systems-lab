using System;
using System.Runtime.CompilerServices;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

public enum RecordQueryStatus
{
    NonExistent,
    StaleOrUncomputed,
    ValidAnswer
}

/// <summary>
/// MEM-10: Shared Validity and Existence Planes
/// Separates existence (bit 0) and computation validity (bit 1) from the raw answer bit.
/// </summary>
public sealed class MEM10_SharedValidityExistencePlanes : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "MEM-VALIDITY-010";
    public string CandidateName => "Shared Validity and Existence Planes (MEM-10)";
    public string Family => "MemoryAndLocality";
    public string Description => "Maintains dedicated 1-bit existence and 1-bit validity planes alongside answer bitmaps.";

    private readonly EapBooleanBitmap _existencePlane;
    private readonly EapBooleanBitmap _validityPlane;
    private readonly EapBooleanBitmap _answerPlane;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _existencePlane.RawMemoryBytes * 3L;
    public long PayloadBytes => _answerPlane.RawMemoryBytes;
    public long MetadataBytes => _existencePlane.RawMemoryBytes + _validityPlane.RawMemoryBytes;

    public MEM10_SharedValidityExistencePlanes(long maxId)
    {
        _maxId = maxId;
        _existencePlane = new EapBooleanBitmap(maxId);
        _validityPlane = new EapBooleanBitmap(maxId);
        _answerPlane = new EapBooleanBitmap(maxId);
    }

    public void RegisterExistence(long id, bool exists) => _existencePlane.SetEligible(id, exists);

    public void SetAnswer(long id, bool answer)
    {
        _existencePlane.SetEligible(id, true);
        _answerPlane.SetEligible(id, answer);
        _validityPlane.SetEligible(id, true);
    }

    public void InvalidateAnswer(long id)
    {
        _validityPlane.SetEligible(id, false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (RecordQueryStatus Status, bool Answer) QueryWithStatus(long id)
    {
        if (!_existencePlane.IsEligible(id))
        {
            return (RecordQueryStatus.NonExistent, false);
        }

        if (!_validityPlane.IsEligible(id))
        {
            return (RecordQueryStatus.StaleOrUncomputed, false);
        }

        return (RecordQueryStatus.ValidAnswer, _answerPlane.IsEligible(id));
    }
}
