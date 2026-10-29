using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.HybridEngine.Contracts;

namespace NeuroCache.ResearchLab.HybridEngine.Routing;

public sealed class HybridQueryRouter : IHybridQueryRouter
{
    private readonly IPointQueryEngine _pointEngine;
    private readonly IAnalyticalQueryEngine _analyticalEngine;
    private readonly IExactFallback _fallbackEngine;
    private readonly ISourceVersionCoordinator _coordinator;
    private readonly IRepresentationPublisher _publisher;
    private readonly HybridDiagnostics _diagnostics = new();
    private bool _isMigrating;

    public HybridDiagnostics Diagnostics => _diagnostics;
    public IPointQueryEngine PointEngine => _pointEngine;
    public IAnalyticalQueryEngine AnalyticalEngine => _analyticalEngine;
    public IExactFallback FallbackEngine => _fallbackEngine;
    public IRepresentationPublisher Publisher => _publisher;
    public bool IsMigrating
    {
        get => Volatile.Read(ref _isMigrating);
        set => Volatile.Write(ref _isMigrating, value);
    }

    public HybridQueryRouter(
        IPointQueryEngine pointEngine,
        IAnalyticalQueryEngine analyticalEngine,
        IExactFallback fallbackEngine,
        ISourceVersionCoordinator coordinator,
        IRepresentationPublisher publisher)
    {
        _pointEngine = pointEngine;
        _analyticalEngine = analyticalEngine;
        _fallbackEngine = fallbackEngine;
        _coordinator = coordinator;
        _publisher = publisher;
    }

    public QueryResult Execute(QueryRequest request)
    {
        return ExecuteAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Guard against in-flight schema migration
        if (IsMigrating)
        {
            _diagnostics.IncFallback();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        // 2. Classify and route
        switch (request.Type)
        {
            case RequestType.PointPredicate:
                return await HandlePointPredicateAsync(request, sw, ct);

            case RequestType.PointAll:
                return await HandlePointAllAsync(request, sw, ct);

            case RequestType.AnalyticalCount:
                return await HandleAnalyticalCountAsync(request, sw, ct);

            case RequestType.AnalyticalIds:
                return await HandleAnalyticalIdsAsync(request, sw, ct);

            case RequestType.AnalyticalBatch:
                return await HandleAnalyticalBatchAsync(request, sw, ct);

            case RequestType.CustomSql:
            default:
                _diagnostics.IncUnsupported();
                return await ExecuteFallbackAsync(request, sw, ct);
        }
    }

    private async Task<QueryResult> HandlePointPredicateAsync(QueryRequest request, Stopwatch sw, CancellationToken ct)
    {
        // Check predicate support (0..7)
        if (!_pointEngine.SupportsPredicate(request.PredicateIndex))
        {
            _diagnostics.IncUnsupported();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        // Enforce Consistency Mode
        bool ready = await CheckFreshnessAsync(request, RoutedEngine.Prod01Point, ct);
        if (!ready)
        {
            _diagnostics.IncStale();
            _diagnostics.IncFallback();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        bool result = _pointEngine.EvaluatePredicate(request.AccountId, request.PredicateIndex);
        sw.Stop();
        _diagnostics.IncPoint();

        return QueryResult.CreatePoint(result, RoutedEngine.Prod01Point, _pointEngine.AppliedVersion, sw.Elapsed.TotalNanoseconds);
    }

    private async Task<QueryResult> HandlePointAllAsync(QueryRequest request, Stopwatch sw, CancellationToken ct)
    {
        bool ready = await CheckFreshnessAsync(request, RoutedEngine.Prod01Point, ct);
        if (!ready)
        {
            _diagnostics.IncStale();
            _diagnostics.IncFallback();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        byte pattern = _pointEngine.QueryAll(request.AccountId);
        sw.Stop();
        _diagnostics.IncPoint();

        return QueryResult.CreatePointAll(pattern, RoutedEngine.Prod01Point, _pointEngine.AppliedVersion, sw.Elapsed.TotalNanoseconds);
    }

    private async Task<QueryResult> HandleAnalyticalCountAsync(QueryRequest request, Stopwatch sw, CancellationToken ct)
    {
        if (request.Analytical == null || !_analyticalEngine.SupportsAnalytical(request.Analytical))
        {
            _diagnostics.IncUnsupported();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        bool ready = await CheckFreshnessAsync(request, RoutedEngine.Thr02Analytical, ct);
        if (!ready)
        {
            _diagnostics.IncStale();
            _diagnostics.IncFallback();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        long count = _analyticalEngine.CountMatching(request.Analytical, request.MaxAccountId);
        sw.Stop();
        _diagnostics.IncAnalytical();

        return QueryResult.CreateAnalyticalCount(count, RoutedEngine.Thr02Analytical, _analyticalEngine.AppliedVersion, sw.Elapsed.TotalNanoseconds);
    }

    private async Task<QueryResult> HandleAnalyticalIdsAsync(QueryRequest request, Stopwatch sw, CancellationToken ct)
    {
        if (request.Analytical == null || !_analyticalEngine.SupportsAnalytical(request.Analytical))
        {
            _diagnostics.IncUnsupported();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        bool ready = await CheckFreshnessAsync(request, RoutedEngine.Thr02Analytical, ct);
        if (!ready)
        {
            _diagnostics.IncStale();
            _diagnostics.IncFallback();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        var ids = _analyticalEngine.GetMatchingIds(request.Analytical, request.MaxAccountId, request.Analytical.Limit);
        sw.Stop();
        _diagnostics.IncAnalytical();

        return QueryResult.CreateAnalyticalIds(ids, RoutedEngine.Thr02Analytical, _analyticalEngine.AppliedVersion, sw.Elapsed.TotalNanoseconds);
    }

    private async Task<QueryResult> HandleAnalyticalBatchAsync(QueryRequest request, Stopwatch sw, CancellationToken ct)
    {
        if (request.Analytical?.BatchThresholds == null || !_analyticalEngine.SupportsAnalytical(request.Analytical))
        {
            _diagnostics.IncUnsupported();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        bool ready = await CheckFreshnessAsync(request, RoutedEngine.Thr02Analytical, ct);
        if (!ready)
        {
            _diagnostics.IncStale();
            _diagnostics.IncFallback();
            return await ExecuteFallbackAsync(request, sw, ct);
        }

        long[] counts = _analyticalEngine.EvaluateThresholdBatch(request.Analytical.BatchThresholds, request.Analytical.RequireActive, request.MaxAccountId);
        sw.Stop();
        _diagnostics.IncAnalytical();

        return QueryResult.CreateAnalyticalBatch(counts, RoutedEngine.Thr02Analytical, _analyticalEngine.AppliedVersion, sw.Elapsed.TotalNanoseconds);
    }

    private async Task<bool> CheckFreshnessAsync(QueryRequest request, RoutedEngine engine, CancellationToken ct)
    {
        long reqVer = request.RequiredVersion;
        if (reqVer <= 0 && request.Consistency == ConsistencyMode.BoundedStaleness)
        {
            // Bounded staleness allows reading local version without waiting
            return true;
        }

        if (reqVer > 0)
        {
            if (_publisher.IsReady(reqVer, engine))
            {
                return true;
            }

            // Must wait or check within budget
            var waitSw = Stopwatch.StartNew();
            var timeout = TimeSpan.FromMilliseconds(request.MaxStalenessMs);
            bool caughtUp = await _coordinator.WaitForVersionAsync(reqVer, timeout, ct);
            waitSw.Stop();
            _diagnostics.AddWait(waitSw.Elapsed);

            return caughtUp && _publisher.IsReady(reqVer, engine);
        }

        // Mode A (SourceCurrent) requires local engine to match published watermark
        long watermark = _coordinator.GetPublishedWatermark();
        return _publisher.IsReady(watermark, engine);
    }

    private async Task<QueryResult> ExecuteFallbackAsync(QueryRequest request, Stopwatch sw, CancellationToken ct)
    {
        _diagnostics.IncFallback();

        if (request.Type == RequestType.PointPredicate)
        {
            bool res = await _fallbackEngine.EvaluatePredicateAsync(request.AccountId, request.PredicateIndex, ct);
            sw.Stop();
            return QueryResult.CreatePoint(res, RoutedEngine.ExactFallbackPostgres, _coordinator.CurrentSourceVersion, sw.Elapsed.TotalNanoseconds);
        }

        if (request.Type == RequestType.AnalyticalCount && request.Analytical != null)
        {
            long count = await _fallbackEngine.CountMatchingAsync(request.Analytical, request.MaxAccountId, ct);
            sw.Stop();
            return QueryResult.CreateAnalyticalCount(count, RoutedEngine.ExactFallbackPostgres, _coordinator.CurrentSourceVersion, sw.Elapsed.TotalNanoseconds);
        }

        if (request.Type == RequestType.CustomSql && !string.IsNullOrWhiteSpace(request.CustomSql))
        {
            var (success, result) = await _fallbackEngine.ExecuteCustomSqlAsync(request.CustomSql, ct);
            sw.Stop();
            return new QueryResult(
                Success: success,
                RoutedTo: RoutedEngine.ExactFallbackPostgres,
                CountResult: result is long l ? l : 0,
                SourceVersion: _coordinator.CurrentSourceVersion,
                ExecutionTimeNs: sw.Elapsed.TotalNanoseconds,
                ErrorMessage: success ? null : result?.ToString());
        }

        sw.Stop();
        return QueryResult.CreateFailure("Unsupported query type could not be executed via fallback.", RoutedEngine.ExactFallbackPostgres);
    }
}
