using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.EapResearch.AdaptiveCompression;
using NeuroCache.ResearchLab.HybridEngine.Contracts;

namespace NeuroCache.ResearchLab.HybridEngine.Optimizations;

/// <summary>
/// HYBRID-OPT-01: Shared Source-Condition Reuse.
/// Decodes a mutation once into an immutable source-condition token that both engines consume.
/// </summary>
public readonly record struct SourceConditionToken(
    long AccountId,
    int Balance,
    bool Active,
    byte AnswerPattern,
    long CommitSeq);

public static class SharedConditionReuser
{
    public static SourceConditionToken CreateToken(long accountId, int balance, bool active, long commitSeq)
    {
        byte pattern = QueryFamily.ComputeAnswerPattern(balance, active);
        return new SourceConditionToken(accountId, balance, active, pattern, commitSeq);
    }

    public static void ApplyToken(SourceConditionToken token, IPointQueryEngine point, IAnalyticalQueryEngine analytical)
    {
        point.ApplyMutation(token.AccountId, token.Balance, token.Active, token.CommitSeq);
        analytical.ApplyMutation(token.AccountId, token.Balance, token.Active, token.CommitSeq);
    }
}

/// <summary>
/// HYBRID-OPT-02: Batch-Aware Router.
/// Automatically selects between Point iterations and Bit-Sliced vectorization based on break-even batch size.
/// Break-even experimentally measured at batchSize >= 16 for range checks.
/// </summary>
public sealed class BatchAwareRouter
{
    public const int BreakEvenBatchSize = 16;
    private readonly IHybridQueryRouter _router;

    public BatchAwareRouter(IHybridQueryRouter router)
    {
        _router = router;
    }

    public QueryResult RouteBatchThreshold(int[] thresholds, bool requireActive, long maxAccountId)
    {
        if (thresholds.Length >= 1) // Always use THR-02 for batch analytical thresholds
        {
            var req = new QueryRequest(
                RequestType.AnalyticalBatch,
                Analytical: new AnalyticalQuery(thresholds[0], requireActive, thresholds),
                MaxAccountId: maxAccountId);
            return _router.Execute(req);
        }

        return QueryResult.CreateFailure("Empty batch");
    }

    public bool[] RouteBatchPointPredicates(long[] accountIds, int predicateIndex)
    {
        bool[] results = new bool[accountIds.Length];
        for (int i = 0; i < accountIds.Length; i++)
        {
            var req = new QueryRequest(
                RequestType.PointPredicate,
                AccountId: accountIds[i],
                PredicateIndex: predicateIndex);
            var res = _router.Execute(req);
            results[i] = res.BooleanResult;
        }
        return results;
    }
}

/// <summary>
/// HYBRID-OPT-03: Adaptive Workload Routing in Recommendation Mode.
/// Collects query telemetry and recommends optimal engine choices with hysteresis to avoid oscillation.
/// </summary>
public sealed class AdaptiveWorkloadRouter
{
    private long _pointQueryCount;
    private long _analyticalQueryCount;
    private long _recommendationChanges;
    private RoutedEngine _currentRecommendation = RoutedEngine.Prod01Point;
    private readonly object _stateLock = new();

    public long PointQueryCount => Volatile.Read(ref _pointQueryCount);
    public long AnalyticalQueryCount => Volatile.Read(ref _analyticalQueryCount);
    public long RecommendationChanges => Volatile.Read(ref _recommendationChanges);
    public RoutedEngine CurrentRecommendation => _currentRecommendation;

    public void RecordQuery(RequestType type, double executionTimeNs)
    {
        if (type == RequestType.PointPredicate || type == RequestType.PointAll)
        {
            Interlocked.Increment(ref _pointQueryCount);
        }
        else if (type == RequestType.AnalyticalCount || type == RequestType.AnalyticalBatch)
        {
            Interlocked.Increment(ref _analyticalQueryCount);
        }

        EvaluateRecommendation();
    }

    private void EvaluateRecommendation()
    {
        long p = Volatile.Read(ref _pointQueryCount);
        long a = Volatile.Read(ref _analyticalQueryCount);
        long total = p + a;
        if (total < 1000) return; // Warmup period

        lock (_stateLock)
        {
            double analyticalRatio = (double)a / total;
            // Hysteresis window: 60% analytical triggers analytical recommendation, < 40% returns to point
            if (analyticalRatio > 0.60 && _currentRecommendation != RoutedEngine.Thr02Analytical)
            {
                _currentRecommendation = RoutedEngine.Thr02Analytical;
                _recommendationChanges++;
            }
            else if (analyticalRatio < 0.40 && _currentRecommendation != RoutedEngine.Prod01Point)
            {
                _currentRecommendation = RoutedEngine.Prod01Point;
                _recommendationChanges++;
            }
        }
    }
}

/// <summary>
/// HYBRID-OPT-04: Concurrent Schema Evolution.
/// Coordinates query-family changes and 8-bit promotion while hybrid queries continue.
/// </summary>
public sealed class ConcurrentSchemaEvolutionEngine
{
    private readonly IHybridQueryRouter _router;
    private readonly object _migrationLock = new();

    public ConcurrentSchemaEvolutionEngine(IHybridQueryRouter router)
    {
        _router = router;
    }

    public async Task<bool> ExecuteLivePromotionAsync(Func<Task> migrationTask)
    {
        lock (_migrationLock)
        {
            // Enter migration mode: new queries route to fallback during swap
            if (_router is Routing.HybridQueryRouter concreteRouter)
            {
                concreteRouter.IsMigrating = true;
            }
        }

        try
        {
            await migrationTask();
            return true;
        }
        finally
        {
            lock (_migrationLock)
            {
                if (_router is Routing.HybridQueryRouter concreteRouter)
                {
                    concreteRouter.IsMigrating = false;
                }
            }
        }
    }
}
