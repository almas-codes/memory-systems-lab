using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.HybridEngine.Routing;
using NeuroCache.ResearchLab.ShadowPilot.Contracts;

namespace NeuroCache.ResearchLab.ShadowPilot.Core;

public sealed class ShadowPilotCoordinator : IDisposable
{
    private readonly ShadowPilotMode _mode;
    private readonly bool _allowLiveShadow;
    private readonly IHybridQueryRouter _hybridRouter;
    private readonly IExactFallback _authoritativeFallback;
    private readonly ShadowCircuitBreaker _circuitBreaker;
    private readonly ShadowQuerySampler _sampler;
    private readonly ShadowRequestNormalizer _normalizer;
    private readonly OracleResultComparator _comparator;
    private readonly HybridShadowExecutor _shadowExecutor;
    private readonly ShadowMetricsCollector _metrics;
    private readonly ShadowRunManifest _manifest;

    public ShadowPilotMode Mode => _mode;
    public ShadowCircuitBreaker CircuitBreaker => _circuitBreaker;
    public ShadowQuerySampler Sampler => _sampler;
    public ShadowMetricsCollector Metrics => _metrics;
    public ShadowRunManifest Manifest => _manifest;

    public ShadowPilotCoordinator(
        ShadowPilotMode mode,
        IHybridQueryRouter hybridRouter,
        IExactFallback authoritativeFallback,
        ShadowRunManifest manifest,
        ShadowCircuitBreakerConfig? config = null,
        double samplingRate = 1.0,
        bool allowLiveShadow = false)
    {
        _mode = mode;
        _allowLiveShadow = allowLiveShadow;

        if (_mode == ShadowPilotMode.Mode3_LiveShadow && !_allowLiveShadow)
        {
            throw new InvalidOperationException(
                "Mode 3 (Live Shadow) is strictly prohibited without explicit administrative authorization and safety validation.");
        }

        var cfg = config ?? new ShadowCircuitBreakerConfig();
        _manifest = manifest;
        _hybridRouter = hybridRouter;
        _authoritativeFallback = authoritativeFallback;
        _circuitBreaker = new ShadowCircuitBreaker(cfg);
        _sampler = new ShadowQuerySampler(cfg, samplingRate, manifest.RandomSeed);
        _normalizer = new ShadowRequestNormalizer(manifest.RecordCount);
        _comparator = new OracleResultComparator();
        _shadowExecutor = new HybridShadowExecutor(hybridRouter, cfg);
        _metrics = new ShadowMetricsCollector();
    }

    public async Task<(object? AuthoritativeResult, ShadowComparisonRecord? Comparison)> ProcessQueryAsync(
        ShadowRequestRecord request,
        CancellationToken ct = default)
    {
        // 1. Authoritative query MUST ALWAYS execute first or concurrently; it is the sole answer returned.
        var swAuth = Stopwatch.StartNew();
        object? authoritativeResult = null;
        try
        {
            switch (request.Type)
            {
                case RequestType.PointPredicate:
                    authoritativeResult = await _authoritativeFallback.EvaluatePredicateAsync(request.AccountId, request.PredicateIndex, ct);
                    break;
                case RequestType.AnalyticalCount:
                    authoritativeResult = request.Analytical != null
                        ? await _authoritativeFallback.CountMatchingAsync(request.Analytical, request.MaxAccountId, ct)
                        : 0L;
                    break;
                case RequestType.PointAll:
                    // Evaluate all 8 predicates authoritatively
                    byte mask = 0;
                    for (int p = 0; p < 8; p++)
                    {
                        if (await _authoritativeFallback.EvaluatePredicateAsync(request.AccountId, p, ct))
                        {
                            mask |= (byte)(1 << p);
                        }
                    }
                    authoritativeResult = mask;
                    break;
                case RequestType.AnalyticalBatch:
                    if (request.Analytical?.BatchThresholds != null)
                    {
                        long[] counts = new long[request.Analytical.BatchThresholds.Length];
                        for (int i = 0; i < request.Analytical.BatchThresholds.Length; i++)
                        {
                            var subQuery = new AnalyticalQuery(request.Analytical.BatchThresholds[i], request.Analytical.RequireActive);
                            counts[i] = await _authoritativeFallback.CountMatchingAsync(subQuery, request.MaxAccountId, ct);
                        }
                        authoritativeResult = counts;
                    }
                    break;
                default:
                    authoritativeResult = false;
                    break;
            }
        }
        catch (Exception ex)
        {
            // Authoritative failure
            swAuth.Stop();
            _circuitBreaker.RecordTimeout();
            return (null, new ShadowComparisonRecord(
                request.RequestId,
                ShadowComparisonOutcome.ShadowError,
                null,
                null,
                swAuth.Elapsed.TotalNanoseconds,
                0,
                0,
                0,
                $"Authoritative execution failure: {ex.Message}"));
        }
        swAuth.Stop();
        double authLatencyNs = swAuth.Elapsed.TotalNanoseconds;
        long authVersion = request.RequiredVersion;

        // 2. Validate & normalize request for shadow path
        var (isValid, isSupported, valError, queryReq) = _normalizer.Normalize(request);
        if (!isValid)
        {
            var invalidRec = new ShadowComparisonRecord(
                request.RequestId,
                ShadowComparisonOutcome.UnsupportedQuery,
                authoritativeResult,
                null,
                authLatencyNs,
                0,
                authVersion,
                0,
                valError);
            _metrics.Record(invalidRec);
            return (authoritativeResult, invalidRec);
        }

        if (!isSupported || queryReq == null)
        {
            var unsuppRec = new ShadowComparisonRecord(
                request.RequestId,
                ShadowComparisonOutcome.UnsupportedQuery,
                authoritativeResult,
                null,
                authLatencyNs,
                0,
                authVersion,
                0,
                valError ?? "Unsupported query family");
            _metrics.Record(unsuppRec);
            return (authoritativeResult, unsuppRec);
        }

        // 3. Safety Check: Circuit Breaker
        if (!_circuitBreaker.CanExecuteShadow())
        {
            var breakerRec = new ShadowComparisonRecord(
                request.RequestId,
                ShadowComparisonOutcome.SkippedCircuitBreaker,
                authoritativeResult,
                null,
                authLatencyNs,
                0,
                authVersion,
                0,
                $"Circuit breaker open: {_circuitBreaker.TripReason}");
            _metrics.Record(breakerRec);
            return (authoritativeResult, breakerRec);
        }

        // 4. Resource Check: Sampler QPS limit
        if (!_sampler.ShouldSample(request.Type))
        {
            var rateRec = new ShadowComparisonRecord(
                request.RequestId,
                ShadowComparisonOutcome.SkippedRateLimit,
                authoritativeResult,
                null,
                authLatencyNs,
                0,
                authVersion,
                0,
                "Skipped by sampling rate or QPS limit");
            _metrics.Record(rateRec);
            return (authoritativeResult, rateRec);
        }

        // 5. Analytical concurrency guard
        IDisposable? analyticalSlot = null;
        if (request.Type == RequestType.AnalyticalCount ||
            request.Type == RequestType.AnalyticalIds ||
            request.Type == RequestType.AnalyticalBatch)
        {
            analyticalSlot = await _sampler.TryAcquireAnalyticalSlotAsync(25, ct);
            if (analyticalSlot == null)
            {
                var semRec = new ShadowComparisonRecord(
                    request.RequestId,
                    ShadowComparisonOutcome.SkippedRateLimit,
                    authoritativeResult,
                    null,
                    authLatencyNs,
                    0,
                    authVersion,
                    0,
                    "Analytical concurrency limit reached");
                _metrics.Record(semRec);
                return (authoritativeResult, semRec);
            }
        }

        // 6. Execute shadow hybrid query with isolated exception and timeout guards
        (ShadowComparisonOutcome execOutcome, QueryResult? shadowResult, double shadowLatencyNs, string? execError) =
            await _shadowExecutor.ExecuteShadowAsync(queryReq, ct);

        // Always release analytical concurrency slot immediately after query completes
        analyticalSlot?.Dispose();

        // 7. Handle shadow errors/timeouts
        if (execOutcome == ShadowComparisonOutcome.ShadowTimeout)
        {
            _circuitBreaker.RecordTimeout();
            var timeoutRec = new ShadowComparisonRecord(
                request.RequestId,
                ShadowComparisonOutcome.ShadowTimeout,
                authoritativeResult,
                null,
                authLatencyNs,
                shadowLatencyNs,
                authVersion,
                0,
                execError);
            _metrics.Record(timeoutRec);
            return (authoritativeResult, timeoutRec);
        }

        if (execOutcome == ShadowComparisonOutcome.ShadowError || shadowResult == null)
        {
            var errRec = new ShadowComparisonRecord(
                request.RequestId,
                ShadowComparisonOutcome.ShadowError,
                authoritativeResult,
                null,
                authLatencyNs,
                shadowLatencyNs,
                authVersion,
                0,
                execError);
            _metrics.Record(errRec);
            return (authoritativeResult, errRec);
        }

        // 8. Compare Authoritative vs Shadow
        long shadowVersion = shadowResult.SourceVersion;
        var comparison = _comparator.Compare(
            request.RequestId,
            request.Type,
            authoritativeResult,
            shadowResult,
            authLatencyNs,
            shadowLatencyNs,
            authVersion,
            shadowVersion);

        // 9. Update circuit breaker state and record metrics
        if (comparison.Outcome == ShadowComparisonOutcome.ExactMatch)
        {
            _circuitBreaker.RecordSuccess();
        }
        else if (comparison.Outcome == ShadowComparisonOutcome.PredicateMismatch)
        {
            _circuitBreaker.RecordMismatch(comparison.Notes ?? "Fidelity mismatch");
        }

        _metrics.Record(comparison);

        // 10. Always return authoritative result to caller
        return (authoritativeResult, comparison);
    }

    public void Dispose()
    {
        _sampler.Dispose();
    }
}
