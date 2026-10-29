using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NeuroCache.ResearchLab.ShadowPilot.Contracts;

namespace NeuroCache.ResearchLab.ShadowPilot.Core;

public sealed class ShadowMetricsCollector
{
    private readonly int _ringBufferSize;
    private readonly ShadowComparisonRecord?[] _ringBuffer;
    private long _writeIndex;

    private readonly ConcurrentBag<ShadowComparisonRecord> _mismatches = new();
    private readonly ConcurrentBag<ShadowComparisonRecord> _timeoutsAndErrors = new();

    private readonly List<double> _authoritativeLatencies = new();
    private readonly List<double> _shadowLatencies = new();
    private readonly object _latencyLock = new();

    private long _countExactMatch;
    private long _countVersionSkew;
    private long _countPredicateMismatch;
    private long _countShadowTimeout;
    private long _countShadowError;
    private long _countSkippedBreaker;
    private long _countSkippedRateLimit;
    private long _countUnsupportedQuery;

    public long TotalExactMatch => Volatile.Read(ref _countExactMatch);
    public long TotalVersionSkew => Volatile.Read(ref _countVersionSkew);
    public long TotalPredicateMismatch => Volatile.Read(ref _countPredicateMismatch);
    public long TotalShadowTimeout => Volatile.Read(ref _countShadowTimeout);
    public long TotalShadowError => Volatile.Read(ref _countShadowError);
    public long TotalSkippedBreaker => Volatile.Read(ref _countSkippedBreaker);
    public long TotalSkippedRateLimit => Volatile.Read(ref _countSkippedRateLimit);
    public long TotalUnsupportedQuery => Volatile.Read(ref _countUnsupportedQuery);

    public long TotalQueriesEvaluated =>
        TotalExactMatch + TotalVersionSkew + TotalPredicateMismatch + TotalShadowTimeout +
        TotalShadowError + TotalSkippedBreaker + TotalSkippedRateLimit + TotalUnsupportedQuery;

    public long TotalCompared => TotalExactMatch + TotalVersionSkew + TotalPredicateMismatch;

    public IReadOnlyList<ShadowComparisonRecord> Mismatches => _mismatches.ToList();
    public IReadOnlyList<ShadowComparisonRecord> TimeoutsAndErrors => _timeoutsAndErrors.ToList();

    public ShadowMetricsCollector(int ringBufferSize = 10_000)
    {
        _ringBufferSize = ringBufferSize > 0 ? ringBufferSize : 10_000;
        _ringBuffer = new ShadowComparisonRecord?[_ringBufferSize];
    }

    public void Record(ShadowComparisonRecord record)
    {
        switch (record.Outcome)
        {
            case ShadowComparisonOutcome.ExactMatch:
                Interlocked.Increment(ref _countExactMatch);
                break;
            case ShadowComparisonOutcome.VersionSkewMismatch:
                Interlocked.Increment(ref _countVersionSkew);
                _mismatches.Add(record);
                break;
            case ShadowComparisonOutcome.PredicateMismatch:
                Interlocked.Increment(ref _countPredicateMismatch);
                _mismatches.Add(record);
                break;
            case ShadowComparisonOutcome.ShadowTimeout:
                Interlocked.Increment(ref _countShadowTimeout);
                _timeoutsAndErrors.Add(record);
                break;
            case ShadowComparisonOutcome.ShadowError:
                Interlocked.Increment(ref _countShadowError);
                _timeoutsAndErrors.Add(record);
                break;
            case ShadowComparisonOutcome.SkippedCircuitBreaker:
                Interlocked.Increment(ref _countSkippedBreaker);
                break;
            case ShadowComparisonOutcome.SkippedRateLimit:
                Interlocked.Increment(ref _countSkippedRateLimit);
                break;
            case ShadowComparisonOutcome.UnsupportedQuery:
                Interlocked.Increment(ref _countUnsupportedQuery);
                break;
        }

        // Add to circular ring buffer
        long idx = Interlocked.Increment(ref _writeIndex) - 1;
        _ringBuffer[idx % _ringBufferSize] = record;

        // Record latencies for valid comparisons
        if (record.Outcome == ShadowComparisonOutcome.ExactMatch ||
            record.Outcome == ShadowComparisonOutcome.VersionSkewMismatch ||
            record.Outcome == ShadowComparisonOutcome.PredicateMismatch)
        {
            lock (_latencyLock)
            {
                if (_authoritativeLatencies.Count < 50_000)
                {
                    _authoritativeLatencies.Add(record.AuthoritativeLatencyNs);
                    _shadowLatencies.Add(record.ShadowLatencyNs);
                }
            }
        }
    }

    public (double p50, double p90, double p95, double p99, double min, double max, double mean) GetAuthoritativeLatencyPercentiles()
    {
        lock (_latencyLock)
        {
            return CalculatePercentiles(_authoritativeLatencies);
        }
    }

    public (double p50, double p90, double p95, double p99, double min, double max, double mean) GetShadowLatencyPercentiles()
    {
        lock (_latencyLock)
        {
            return CalculatePercentiles(_shadowLatencies);
        }
    }

    private static (double p50, double p90, double p95, double p99, double min, double max, double mean) CalculatePercentiles(List<double> values)
    {
        if (values.Count == 0) return (0, 0, 0, 0, 0, 0, 0);

        var sorted = values.OrderBy(v => v).ToList();
        double min = sorted[0];
        double max = sorted[^1];
        double mean = sorted.Average();

        double GetP(double percentile)
        {
            int idx = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
        }

        return (GetP(0.50), GetP(0.90), GetP(0.95), GetP(0.99), min, max, mean);
    }

    public void Reset()
    {
        Volatile.Write(ref _countExactMatch, 0);
        Volatile.Write(ref _countVersionSkew, 0);
        Volatile.Write(ref _countPredicateMismatch, 0);
        Volatile.Write(ref _countShadowTimeout, 0);
        Volatile.Write(ref _countShadowError, 0);
        Volatile.Write(ref _countSkippedBreaker, 0);
        Volatile.Write(ref _countSkippedRateLimit, 0);
        Volatile.Write(ref _countUnsupportedQuery, 0);
        Volatile.Write(ref _writeIndex, 0);
        Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
        _mismatches.Clear();
        _timeoutsAndErrors.Clear();
        lock (_latencyLock)
        {
            _authoritativeLatencies.Clear();
            _shadowLatencies.Clear();
        }
    }
}
