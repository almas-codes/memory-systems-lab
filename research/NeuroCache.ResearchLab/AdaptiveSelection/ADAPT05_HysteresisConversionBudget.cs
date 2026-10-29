using System;
using System.Diagnostics;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.AdaptiveSelection;

/// <summary>
/// ADAPT-05: Hysteresis and Conversion Budget
/// Protects against unstable switching and thrashing by enforcing minimum observation time, conversion memory budgets, and cool-downs.
/// </summary>
public sealed class ADAPT05_HysteresisConversionBudget : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "ADAPT-HYSTERESIS-005";
    public string CandidateName => "Hysteresis and Conversion Budget (ADAPT-05)";
    public string Family => "AdaptiveSelection";
    public string Description => "Enforces switching hysteresis, cool-downs, and memory ceilings to prevent representation thrashing.";

    private readonly TimeSpan _minStabilizationWindow;
    private readonly long _maxConversionMemoryBytes;
    private DateTime _lastConversionTimeUtc = DateTime.MinValue;
    private int _conversionsCompleted;

    public int ConversionsCompleted => _conversionsCompleted;
    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 32;
    public long MetadataBytes => 96;

    public ADAPT05_HysteresisConversionBudget(
        TimeSpan? minStabilizationWindow = null,
        long maxConversionMemoryBytes = 100_000_000)
    {
        _minStabilizationWindow = minStabilizationWindow ?? TimeSpan.FromSeconds(5);
        _maxConversionMemoryBytes = maxConversionMemoryBytes;
    }

    public bool CanInitiateConversion(long estimatedPeakMemoryBytes)
    {
        // 1. Check memory ceiling
        if (estimatedPeakMemoryBytes > _maxConversionMemoryBytes)
        {
            return false; // Conversion exceeds memory budget!
        }

        // 2. Check cool-down / hysteresis window
        if (DateTime.UtcNow - _lastConversionTimeUtc < _minStabilizationWindow)
        {
            return false; // Flapping prevention: within stabilization window
        }

        return true;
    }

    public void AcknowledgeConversionCompleted()
    {
        _lastConversionTimeUtc = DateTime.UtcNow;
        _conversionsCompleted++;
    }
}
