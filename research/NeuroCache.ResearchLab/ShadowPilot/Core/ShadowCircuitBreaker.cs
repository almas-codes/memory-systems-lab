using System;
using System.Threading;
using NeuroCache.ResearchLab.ShadowPilot.Contracts;

namespace NeuroCache.ResearchLab.ShadowPilot.Core;

public sealed class ShadowCircuitBreaker
{
    private readonly ShadowCircuitBreakerConfig _config;
    private int _state; // 0 = Closed, 1 = Tripped
    private int _consecutiveMismatches;
    private int _timeoutCount;
    private long _totalComparisons;
    private long _totalMismatches;
    private string? _tripReason;
    private DateTime? _trippedTimestampUtc;

    public bool IsClosed => Volatile.Read(ref _state) == 0;
    public bool IsTripped => Volatile.Read(ref _state) == 1;
    public string? TripReason => _tripReason;
    public DateTime? TrippedTimestampUtc => _trippedTimestampUtc;
    public int ConsecutiveMismatches => Volatile.Read(ref _consecutiveMismatches);
    public int TimeoutCount => Volatile.Read(ref _timeoutCount);
    public long TotalComparisons => Volatile.Read(ref _totalComparisons);
    public long TotalMismatches => Volatile.Read(ref _totalMismatches);

    public ShadowCircuitBreaker(ShadowCircuitBreakerConfig? config = null)
    {
        _config = config ?? new ShadowCircuitBreakerConfig();
        _state = 0; // Closed
    }

    public bool CanExecuteShadow()
    {
        return Volatile.Read(ref _state) == 0;
    }

    public void RecordSuccess()
    {
        Interlocked.Increment(ref _totalComparisons);
        Volatile.Write(ref _consecutiveMismatches, 0);
    }

    public void RecordMismatch(string reason)
    {
        Interlocked.Increment(ref _totalComparisons);
        long mismatches = Interlocked.Increment(ref _totalMismatches);
        int consecutive = Interlocked.Increment(ref _consecutiveMismatches);

        if (consecutive >= _config.MaxConsecutiveMismatches)
        {
            Trip($"Consecutive mismatches ({consecutive}) reached threshold ({_config.MaxConsecutiveMismatches}): {reason}");
            return;
        }

        long total = Volatile.Read(ref _totalComparisons);
        if (total >= 100)
        {
            double rate = ((double)mismatches / total) * 100.0;
            if (rate > _config.MaxMismatchRatePercent)
            {
                Trip($"Mismatch rate ({rate:F2}%) exceeded limit ({_config.MaxMismatchRatePercent}%): {reason}");
            }
        }
    }

    public void RecordTimeout()
    {
        int timeouts = Interlocked.Increment(ref _timeoutCount);
        if (timeouts >= _config.MaxDatabaseTimeouts)
        {
            Trip($"Database/Shadow timeouts ({timeouts}) reached threshold ({_config.MaxDatabaseTimeouts})");
        }
    }

    public void RecordLag(long lagEvents)
    {
        if (lagEvents > _config.MaxSourceLagEvents)
        {
            Trip($"Source lag ({lagEvents} events) exceeded maximum limit ({_config.MaxSourceLagEvents})");
        }
    }

    public void KillSwitch(string manualReason = "Manual operator kill switch triggered")
    {
        Trip(manualReason);
    }

    private void Trip(string reason)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
        {
            _tripReason = reason;
            _trippedTimestampUtc = DateTime.UtcNow;
        }
    }

    public bool Reset(string operatorVerification)
    {
        if (string.IsNullOrWhiteSpace(operatorVerification))
        {
            return false;
        }

        Volatile.Write(ref _consecutiveMismatches, 0);
        Volatile.Write(ref _timeoutCount, 0);
        Volatile.Write(ref _totalMismatches, 0);
        Volatile.Write(ref _totalComparisons, 0);
        _tripReason = null;
        _trippedTimestampUtc = null;
        Volatile.Write(ref _state, 0); // Re-close breaker
        return true;
    }
}
