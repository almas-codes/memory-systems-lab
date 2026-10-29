using System;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.AdaptiveSelection;

/// <summary>
/// ADAPT-02: Admission-Controlled Promotion (ACRP)
/// Enforces observation windows and minimum frequency thresholds before permitting expensive materialization.
/// </summary>
public sealed class ADAPT02_AdmissionControlledPromotion : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "ADAPT-ACRP-002";
    public string CandidateName => "Admission-Controlled Promotion (ACRP / ADAPT-02)";
    public string Family => "AdaptiveSelection";
    public string Description => "Requires sustained query benefit over observation window before promoting representation.";

    private readonly int _promotionThreshold;
    private long _observationQueries;
    private bool _isPromoted;

    public long ObservedQueries => Volatile.Read(ref _observationQueries);
    public bool IsPromoted => _isPromoted;

    public long TotalAllocatedBytes => 64;
    public long PayloadBytes => 16;
    public long MetadataBytes => 48;

    public ADAPT02_AdmissionControlledPromotion(int promotionThreshold = 10_000)
    {
        _promotionThreshold = promotionThreshold;
    }

    public bool RecordAccessAndCheckAdmission()
    {
        if (_isPromoted) return true;

        long q = Interlocked.Increment(ref _observationQueries);
        if (q >= _promotionThreshold)
        {
            _isPromoted = true;
            return true; // Admitted for materialization!
        }

        return false; // Cold: keep in compute/oracle path
    }

    public void Reset()
    {
        _isPromoted = false;
        Volatile.Write(ref _observationQueries, 0);
    }
}
