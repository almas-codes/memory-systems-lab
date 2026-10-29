using System;
using System.Collections.Generic;
using System.Threading;
using NeuroCache.ResearchLab.HybridEngine.Contracts;

namespace NeuroCache.ResearchLab.HybridEngine.Coordination;

/// <summary>
/// Variant A: Serialized Publication.
/// Applies source event to all affected representations and publishes common logical version only when complete.
/// </summary>
public sealed class SerializedPublicationCoordinator : IRepresentationPublisher
{
    private readonly IPointQueryEngine _pointEngine;
    private readonly IAnalyticalQueryEngine _analyticalEngine;
    private long _publishedVersion;
    private readonly object _publishLock = new();

    public string VariantName => "Variant A: Serialized Publication";
    public long PublishedVersion => Volatile.Read(ref _publishedVersion);

    public SerializedPublicationCoordinator(IPointQueryEngine pointEngine, IAnalyticalQueryEngine analyticalEngine, long initialVersion = 0)
    {
        _pointEngine = pointEngine;
        _analyticalEngine = analyticalEngine;
        _publishedVersion = initialVersion;
    }

    public void PublishMutation(long commitSeq, long accountId, int balance, bool active)
    {
        lock (_publishLock)
        {
            _pointEngine.ApplyMutation(accountId, balance, active, commitSeq);
            _analyticalEngine.ApplyMutation(accountId, balance, active, commitSeq);
            long cur = Volatile.Read(ref _publishedVersion);
            if (commitSeq > cur) Volatile.Write(ref _publishedVersion, commitSeq);
        }
    }

    public void PublishBatch(long commitSeq, IEnumerable<(long accountId, int balance, bool active)> mutations)
    {
        lock (_publishLock)
        {
            foreach (var (id, bal, act) in mutations)
            {
                _pointEngine.ApplyMutation(id, bal, act, commitSeq);
                _analyticalEngine.ApplyMutation(id, bal, act, commitSeq);
            }
            long cur = Volatile.Read(ref _publishedVersion);
            if (commitSeq > cur) Volatile.Write(ref _publishedVersion, commitSeq);
        }
    }

    public bool IsReady(long requiredVersion, RoutedEngine engine)
    {
        return Volatile.Read(ref _publishedVersion) >= requiredVersion;
    }
}

/// <summary>
/// Variant B: Generational Publication.
/// Updates representations under a new generation, then publishes the generation reference atomically.
/// </summary>
public sealed class GenerationalPublicationCoordinator : IRepresentationPublisher
{
    private readonly IPointQueryEngine _pointEngine;
    private readonly IAnalyticalQueryEngine _analyticalEngine;
    private long _activeGeneration;
    private readonly object _genLock = new();

    public string VariantName => "Variant B: Generational Publication";
    public long PublishedVersion => Volatile.Read(ref _activeGeneration);

    public GenerationalPublicationCoordinator(IPointQueryEngine pointEngine, IAnalyticalQueryEngine analyticalEngine, long initialGeneration = 0)
    {
        _pointEngine = pointEngine;
        _analyticalEngine = analyticalEngine;
        _activeGeneration = initialGeneration;
    }

    public void PublishMutation(long commitSeq, long accountId, int balance, bool active)
    {
        lock (_genLock)
        {
            _pointEngine.ApplyMutation(accountId, balance, active, commitSeq);
            _analyticalEngine.ApplyMutation(accountId, balance, active, commitSeq);
            long cur = Volatile.Read(ref _activeGeneration);
            if (commitSeq > cur) Interlocked.Exchange(ref _activeGeneration, commitSeq);
        }
    }

    public void PublishBatch(long commitSeq, IEnumerable<(long accountId, int balance, bool active)> mutations)
    {
        lock (_genLock)
        {
            foreach (var (id, bal, act) in mutations)
            {
                _pointEngine.ApplyMutation(id, bal, act, commitSeq);
                _analyticalEngine.ApplyMutation(id, bal, act, commitSeq);
            }
            long cur = Volatile.Read(ref _activeGeneration);
            if (commitSeq > cur) Interlocked.Exchange(ref _activeGeneration, commitSeq);
        }
    }

    public bool IsReady(long requiredVersion, RoutedEngine engine)
    {
        return Volatile.Read(ref _activeGeneration) >= requiredVersion;
    }
}

/// <summary>
/// Variant C: Per-Domain Readiness.
/// Tracks applied versions independently for point and analytical representations.
/// </summary>
public sealed class PerDomainReadinessCoordinator : IRepresentationPublisher
{
    private readonly IPointQueryEngine _pointEngine;
    private readonly IAnalyticalQueryEngine _analyticalEngine;
    private long _pointVersion;
    private long _analyticalVersion;
    private readonly object _updateLock = new();

    public string VariantName => "Variant C: Per-Domain Readiness";
    public long PublishedVersion => Math.Min(Volatile.Read(ref _pointVersion), Volatile.Read(ref _analyticalVersion));
    public long PointVersion => Volatile.Read(ref _pointVersion);
    public long AnalyticalVersion => Volatile.Read(ref _analyticalVersion);

    public PerDomainReadinessCoordinator(IPointQueryEngine pointEngine, IAnalyticalQueryEngine analyticalEngine, long initialVersion = 0)
    {
        _pointEngine = pointEngine;
        _analyticalEngine = analyticalEngine;
        _pointVersion = initialVersion;
        _analyticalVersion = initialVersion;
    }

    public void PublishMutation(long commitSeq, long accountId, int balance, bool active)
    {
        lock (_updateLock)
        {
            _pointEngine.ApplyMutation(accountId, balance, active, commitSeq);
            long curPt = Volatile.Read(ref _pointVersion);
            if (commitSeq > curPt) Volatile.Write(ref _pointVersion, commitSeq);

            _analyticalEngine.ApplyMutation(accountId, balance, active, commitSeq);
            long curAn = Volatile.Read(ref _analyticalVersion);
            if (commitSeq > curAn) Volatile.Write(ref _analyticalVersion, commitSeq);
        }
    }

    public void PublishBatch(long commitSeq, IEnumerable<(long accountId, int balance, bool active)> mutations)
    {
        lock (_updateLock)
        {
            foreach (var (id, bal, act) in mutations)
            {
                _pointEngine.ApplyMutation(id, bal, act, commitSeq);
                _analyticalEngine.ApplyMutation(id, bal, act, commitSeq);
            }
            long curPt = Volatile.Read(ref _pointVersion);
            if (commitSeq > curPt) Volatile.Write(ref _pointVersion, commitSeq);

            long curAn = Volatile.Read(ref _analyticalVersion);
            if (commitSeq > curAn) Volatile.Write(ref _analyticalVersion, commitSeq);
        }
    }

    public bool IsReady(long requiredVersion, RoutedEngine engine)
    {
        return engine switch
        {
            RoutedEngine.Prod01Point => Volatile.Read(ref _pointVersion) >= requiredVersion,
            RoutedEngine.Thr02Analytical => Volatile.Read(ref _analyticalVersion) >= requiredVersion,
            _ => PublishedVersion >= requiredVersion
        };
    }
}
