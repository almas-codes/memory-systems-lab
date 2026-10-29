using System;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.SemanticEncoding;
using NeuroCache.ResearchLab.ThresholdEncoding;

namespace NeuroCache.ResearchLab.AdaptiveSelection;

/// <summary>
/// ADAPT-04: Portfolio Router per Query Family (PRQF)
/// Routes declared query types to their optimal, independently qualified representation.
/// </summary>
public sealed class ADAPT04_PortfolioRouter : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "ADAPT-PORTFOLIO-004";
    public string CandidateName => "Portfolio Router per Query Family (PRQF / ADAPT-04)";
    public string Family => "AdaptiveSelection";
    public string Description => "Dispatches queries across portfolio of proven representations (Bitmaps, Semantic Planes, Threshold Directories).";

    private readonly EapBooleanBitmap _singleBooleanBitmap;
    private readonly SEM03_FourBitSemanticClasses _semanticPlane;
    private readonly THR01_ThresholdDecisionDirectory _thresholdDir;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _singleBooleanBitmap.RawMemoryBytes + _semanticPlane.TotalAllocatedBytes + _thresholdDir.TotalAllocatedBytes;
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 256;

    public ADAPT04_PortfolioRouter(long maxId)
    {
        _maxId = maxId;
        _singleBooleanBitmap = new EapBooleanBitmap(maxId);
        _semanticPlane = new SEM03_FourBitSemanticClasses(maxId);
        _thresholdDir = new THR01_ThresholdDecisionDirectory(maxId);
    }

    public void HydrateAccount(long id, int balance, bool active)
    {
        _singleBooleanBitmap.SetEligible(id, active && balance >= 100);
        _semanticPlane.HandleMutation(id, balance, active, 0);
        _thresholdDir.SetBalance(id, balance);
    }

    public bool QuerySinglePredicate(long id) => _singleBooleanBitmap.IsEligible(id);

    public byte QueryJointFamily(long id) => _semanticPlane.Query(id);

    public bool QueryThreshold(long id, int threshold) => _thresholdDir.EvaluateThreshold(id, threshold);
}
