using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroCache.ResearchLab.HybridEngine.Contracts;

public interface IPointQueryEngine
{
    string EngineName { get; }
    long AppliedVersion { get; }
    long MaxSupportedId { get; }
    long TotalAllocatedBytes { get; }
    bool SupportsPredicate(int predicateIndex);
    bool EvaluatePredicate(long accountId, int predicateIndex);
    byte QueryAll(long accountId);
    void ApplyMutation(long accountId, int balance, bool active, long commitSeq);
    void Rebuild(long maxId, IEnumerable<(long id, int balance, bool active)> records, long snapshotVersion);
}

public interface IAnalyticalQueryEngine
{
    string EngineName { get; }
    long AppliedVersion { get; }
    long MaxSupportedId { get; }
    long TotalAllocatedBytes { get; }
    bool SupportsAnalytical(AnalyticalQuery query);
    long CountMatching(AnalyticalQuery query, long maxAccountId);
    List<long> GetMatchingIds(AnalyticalQuery query, long maxAccountId, int limit);
    long[] EvaluateThresholdBatch(int[] thresholds, bool requireActive, long maxAccountId);
    void ApplyMutation(long accountId, int balance, bool active, long commitSeq);
    void Rebuild(long maxId, IEnumerable<(long id, int balance, bool active)> records, long snapshotVersion);
}

public interface IExactFallback
{
    string EngineName { get; }
    Task<bool> EvaluatePredicateAsync(long accountId, int predicateIndex, CancellationToken ct = default);
    Task<long> CountMatchingAsync(AnalyticalQuery query, long maxAccountId, CancellationToken ct = default);
    Task<(bool Success, object? Result)> ExecuteCustomSqlAsync(string sql, CancellationToken ct = default);
}

public interface ISourceVersionCoordinator
{
    long CurrentSourceVersion { get; }
    long AllocateCommitSequence();
    void CommitTransaction(long commitSeq, IEnumerable<(long accountId, int balance, bool active)> mutations);
    Task<bool> WaitForVersionAsync(long requiredVersion, TimeSpan timeout, CancellationToken ct = default);
    long GetPublishedWatermark();
}

public interface IRepresentationPublisher
{
    string VariantName { get; }
    long PublishedVersion { get; }
    void PublishMutation(long commitSeq, long accountId, int balance, bool active);
    void PublishBatch(long commitSeq, IEnumerable<(long accountId, int balance, bool active)> mutations);
    bool IsReady(long requiredVersion, RoutedEngine engine);
}

public sealed class HybridDiagnostics
{
    private long _pointQueriesRoutedToProd01;
    private long _analyticalQueriesRoutedToThr02;
    private long _exactFallbacksExecuted;
    private long _unsupportedQueriesRejected;
    private long _staleVersionDetections;
    private long _versionWaitDurationTicks;
    private long _versionWaitCount;
    private long _versionMismatches;
    private long _rebuildsTriggered;

    public long PointQueriesRoutedToProd01 => Volatile.Read(ref _pointQueriesRoutedToProd01);
    public long AnalyticalQueriesRoutedToThr02 => Volatile.Read(ref _analyticalQueriesRoutedToThr02);
    public long ExactFallbacksExecuted => Volatile.Read(ref _exactFallbacksExecuted);
    public long UnsupportedQueriesRejected => Volatile.Read(ref _unsupportedQueriesRejected);
    public long StaleVersionDetections => Volatile.Read(ref _staleVersionDetections);
    public long VersionWaitCount => Volatile.Read(ref _versionWaitCount);
    public double TotalWaitMilliseconds => (double)Volatile.Read(ref _versionWaitDurationTicks) / TimeSpan.TicksPerMillisecond;
    public long VersionMismatches => Volatile.Read(ref _versionMismatches);
    public long RebuildsTriggered => Volatile.Read(ref _rebuildsTriggered);

    public void IncPoint() => Interlocked.Increment(ref _pointQueriesRoutedToProd01);
    public void IncAnalytical() => Interlocked.Increment(ref _analyticalQueriesRoutedToThr02);
    public void IncFallback() => Interlocked.Increment(ref _exactFallbacksExecuted);
    public void IncUnsupported() => Interlocked.Increment(ref _unsupportedQueriesRejected);
    public void IncStale() => Interlocked.Increment(ref _staleVersionDetections);
    public void AddWait(TimeSpan duration)
    {
        Interlocked.Increment(ref _versionWaitCount);
        Interlocked.Add(ref _versionWaitDurationTicks, duration.Ticks);
    }
    public void IncMismatch() => Interlocked.Increment(ref _versionMismatches);
    public void IncRebuild() => Interlocked.Increment(ref _rebuildsTriggered);

    public void Reset()
    {
        Volatile.Write(ref _pointQueriesRoutedToProd01, 0);
        Volatile.Write(ref _analyticalQueriesRoutedToThr02, 0);
        Volatile.Write(ref _exactFallbacksExecuted, 0);
        Volatile.Write(ref _unsupportedQueriesRejected, 0);
        Volatile.Write(ref _staleVersionDetections, 0);
        Volatile.Write(ref _versionWaitDurationTicks, 0);
        Volatile.Write(ref _versionWaitCount, 0);
        Volatile.Write(ref _versionMismatches, 0);
        Volatile.Write(ref _rebuildsTriggered, 0);
    }
}

public interface IHybridQueryRouter
{
    QueryResult Execute(QueryRequest request);
    Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken ct = default);
    HybridDiagnostics Diagnostics { get; }
    IPointQueryEngine PointEngine { get; }
    IAnalyticalQueryEngine AnalyticalEngine { get; }
    IExactFallback FallbackEngine { get; }
    IRepresentationPublisher Publisher { get; }
}
