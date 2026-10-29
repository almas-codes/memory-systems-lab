using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.ShadowPilot.Contracts;

namespace NeuroCache.ResearchLab.ShadowPilot.Core;

public sealed class ShadowQuerySampler : IDisposable
{
    private readonly ShadowCircuitBreakerConfig _config;
    private readonly SemaphoreSlim _analyticalSemaphore;
    private readonly double _samplingRate; // 0.0 to 1.0
    private readonly Random _random;

    private long _totalRequests;
    private long _sampledRequests;
    private long _rateLimitedRequests;

    // Sliding window / interval token counter for rate limiting
    private long _intervalStartTimestamp;
    private long _requestsInCurrentInterval;
    private readonly long _intervalTicks;

    public long TotalRequests => Volatile.Read(ref _totalRequests);
    public long SampledRequests => Volatile.Read(ref _sampledRequests);
    public long RateLimitedRequests => Volatile.Read(ref _rateLimitedRequests);

    public ShadowQuerySampler(ShadowCircuitBreakerConfig? config = null, double samplingRate = 1.0, int seed = 42)
    {
        _config = config ?? new ShadowCircuitBreakerConfig();
        _analyticalSemaphore = new SemaphoreSlim(_config.MaxConcurrentAnalyticalQueries, _config.MaxConcurrentAnalyticalQueries);
        _samplingRate = Math.Clamp(samplingRate, 0.0, 1.0);
        _random = new Random(seed);
        _intervalStartTimestamp = Stopwatch.GetTimestamp();
        _intervalTicks = Stopwatch.Frequency; // 1 second intervals
    }

    public bool ShouldSample(RequestType type)
    {
        Interlocked.Increment(ref _totalRequests);

        // 1. Probabilistic sampling check
        if (_samplingRate < 1.0)
        {
            lock (_random)
            {
                if (_random.NextDouble() > _samplingRate)
                {
                    Interlocked.Increment(ref _rateLimitedRequests);
                    return false;
                }
            }
        }

        // 2. QPS rate limit check
        long now = Stopwatch.GetTimestamp();
        long windowStart = Volatile.Read(ref _intervalStartTimestamp);
        if (now - windowStart > _intervalTicks)
        {
            // Reset interval
            Volatile.Write(ref _intervalStartTimestamp, now);
            Volatile.Write(ref _requestsInCurrentInterval, 1);
        }
        else
        {
            long currentCount = Interlocked.Increment(ref _requestsInCurrentInterval);
            if (currentCount > _config.MaxShadowQueryRatePerSec)
            {
                Interlocked.Increment(ref _rateLimitedRequests);
                return false;
            }
        }

        Interlocked.Increment(ref _sampledRequests);
        return true;
    }

    public async Task<IDisposable?> TryAcquireAnalyticalSlotAsync(int timeoutMs = 25, CancellationToken ct = default)
    {
        try
        {
            bool acquired = await _analyticalSemaphore.WaitAsync(timeoutMs, ct);
            if (acquired)
            {
                return new AnalyticalSlotReleaser(_analyticalSemaphore);
            }
        }
        catch
        {
            // Timeout or cancellation
        }

        Interlocked.Increment(ref _rateLimitedRequests);
        return null;
    }

    public void Dispose()
    {
        _analyticalSemaphore.Dispose();
    }

    private sealed class AnalyticalSlotReleaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public AnalyticalSlotReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _semaphore, null);
            sem?.Release();
        }
    }
}
