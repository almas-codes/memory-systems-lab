using System;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.AdaptiveSelection;

public enum RecommendedRepresentation
{
    SingleBooleanBitmap,
    SemanticClassPlane4Bit,
    PackedByteVector,
    OnTheFlyComputation
}

/// <summary>
/// ADAPT-01: Workload-Telemetry Cost-Model Controller (WTCC)
/// Collects query family metrics and predicts optimal representation based on empirical cost models.
/// </summary>
public sealed class ADAPT01_WorkloadTelemetryCostModel : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "ADAPT-WTCC-001";
    public string CandidateName => "Workload-Telemetry Cost-Model Controller (WTCC / ADAPT-01)";
    public string Family => "AdaptiveSelection";
    public string Description => "Predicts optimal representation based on read/write ratio, query count, and memory constraints.";

    private long _queryCount;
    private long _mutationCount;
    private int _declaredQueries = 1;
    private int _distinctObservedClasses = 1;

    public long QueryCount => Volatile.Read(ref _queryCount);
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 32;
    public long MetadataBytes => 96;

    public void ConfigureQueryFamily(int declaredQueryCount, int distinctObservedClasses)
    {
        _declaredQueries = declaredQueryCount;
        _distinctObservedClasses = distinctObservedClasses;
    }

    public void RecordQuery() => Interlocked.Increment(ref _queryCount);
    public void RecordMutation() => Interlocked.Increment(ref _mutationCount);

    public RecommendedRepresentation RecommendRepresentation()
    {
        long q = QueryCount;
        long m = MutationCount;

        if (q + m < 100) return RecommendedRepresentation.SingleBooleanBitmap; // Warmup

        // If writes heavily exceed queries, on-the-fly compute avoids cache maintenance
        if (m > q * 5) return RecommendedRepresentation.OnTheFlyComputation;

        if (_declaredQueries == 1)
        {
            return RecommendedRepresentation.SingleBooleanBitmap;
        }

        if (_distinctObservedClasses <= 16)
        {
            return RecommendedRepresentation.SemanticClassPlane4Bit;
        }

        return RecommendedRepresentation.PackedByteVector;
    }
}
