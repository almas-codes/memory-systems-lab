using System;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.QueryExecution;

public enum ExecutionMode
{
    MaterializeInBitmap,
    ComputeOnTheFly
}

/// <summary>
/// EXEC-06: Cost-Adaptive Store-vs-Compute Switch (CASC)
/// Dynamically selects between materializing answers or computing on-the-fly based on read/write ratio and cost model.
/// </summary>
public sealed class EXEC06_CostAdaptiveStoreVsComputeSwitch : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "EXEC-CASC-006";
    public string CandidateName => "Cost-Adaptive Store-vs-Compute Switch (CASC)";
    public string Family => "QueryExecution";
    public string Description => "Chooses between materialized storage and on-the-fly computation based on workload telemetry and cost model.";

    private long _readCount;
    private long _writeCount;
    private volatile ExecutionMode _currentMode = ExecutionMode.ComputeOnTheFly;

    public long ReadCount => Volatile.Read(ref _readCount);
    public long WriteCount => Volatile.Read(ref _writeCount);
    public ExecutionMode CurrentMode => _currentMode;

    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 32;
    public long MetadataBytes => 96;

    public void RecordRead()
    {
        Interlocked.Increment(ref _readCount);
        EvaluateMode();
    }

    public void RecordWrite()
    {
        Interlocked.Increment(ref _writeCount);
        EvaluateMode();
    }

    private void EvaluateMode()
    {
        long r = ReadCount;
        long w = WriteCount;
        if (r + w < 100) return; // Warmup window

        double readWriteRatio = w == 0 ? double.MaxValue : (double)r / w;

        // If reads dominate writes (> 5:1), materializing in memory pays for itself.
        // If writes dominate reads (< 1:1), on-the-fly computation avoids wasteful cache invalidation/maintenance.
        if (readWriteRatio > 5.0 && _currentMode != ExecutionMode.MaterializeInBitmap)
        {
            _currentMode = ExecutionMode.MaterializeInBitmap;
        }
        else if (readWriteRatio < 1.0 && _currentMode != ExecutionMode.ComputeOnTheFly)
        {
            _currentMode = ExecutionMode.ComputeOnTheFly;
        }
    }
}
