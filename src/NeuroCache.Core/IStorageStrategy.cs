using System;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroCache.Core;

public sealed class StrategyOptions
{
    public long RamBudgetBytes { get; set; } = 256 * 1024 * 1024; // Default 256 MB
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
    public bool EnableRecomputation { get; set; } = true;
    public bool EnableSharedWitnesses { get; set; } = true;
    public bool EnableCapsules { get; set; } = true;
    public bool EnablePrefetch { get; set; } = false;
    public bool EnablePrediction { get; set; } = true;
    public int PredictionLearningRatePercent { get; set; } = 5; // Max 5% CPU
    public int BoundedQueueDepth { get; set; } = 1024;
}

public sealed class StrategyStats
{
    public long TotalRequests;
    public long Hits;
    public long Misses;
    public long Fallbacks;
    public long InlineReuses;
    public long Recomputes;
    public long CapsuleReads;
    public long ExtentReads;
    public long MemoryMappedReads;
    public long PhysicalReads;
    public long PhysicalBytesRead;
    public long PhysicalWrites;
    public long PhysicalBytesWritten;
    public long ResidentBytes;
    public long Evictions;
    public long Invalidations;
    public long CorrectnessViolations;

    public double HitRate => TotalRequests > 0 ? (double)Hits / TotalRequests : 0.0;
}

[Flags]
public enum StrategyCapabilities
{
    None = 0,
    FullPayload = 1 << 0,
    FieldProjection = 1 << 1,
    PureRecomputation = 1 << 2,
    SharedWitnesses = 1 << 3,
    StorageCapsules = 1 << 4,
    MemoryMapped = 1 << 5,
    StrictRamBounded = 1 << 6,
    Persistence = 1 << 7
}

public interface IStorageStrategy : IAsyncDisposable
{
    string Name { get; }
    StrategyCapabilities Capabilities { get; }
    ValueTask InitializeAsync(string dataDirectory, RecordManifest manifest, StrategyOptions options, CancellationToken ct = default);
    ValueTask<StrategyResult> GetAsync(ulong key, CancellationToken ct = default);
    ValueTask<StrategyResult> GetProjectionAsync(ulong key, int fieldIndex, CancellationToken ct = default);
    ValueTask<StrategyResult> ComputeDerivedAsync(ulong key, ulong param, CancellationToken ct = default);
    ValueTask<StrategyResult> EvaluatePredicateAsync(ulong key, ulong threshold, CancellationToken ct = default);
    ValueTask<bool> UpsertAsync(DataRecord record, CancellationToken ct = default);
    ValueTask InvalidateAsync(ulong key, CancellationToken ct = default);
    StrategyStats GetStats();
    void ResetStats();
}
