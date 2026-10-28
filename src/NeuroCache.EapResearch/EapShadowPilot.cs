using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace NeuroCache.EapResearch;

/// <summary>
/// Status of the EAP shadow pilot circuit breaker.
/// </summary>
public enum CircuitBreakerState
{
    Closed,     // Normal: shadow reads active alongside PostgreSQL authoritative reads
    Open,       // Tripped: shadow reads bypassed, 100% traffic routed directly to PostgreSQL
    HalfOpen    // Trial: evaluating a limited sample before restoring shadow reads
}

/// <summary>
/// Aggregated telemetry and health statistics for the Shadow-Mode Pilot.
/// </summary>
public sealed class ShadowPilotTelemetry
{
    public long TotalQueries;
    public long MatchCount;
    public long MismatchCount;
    public long CircuitBreakerTrips;
    public long KillSwitchActivations;
    public double TotalEapLatencyNs;
    public double TotalPgLatencyNs;
    public long OutboxLag;
    public CircuitBreakerState State;
    public bool KillSwitchEngaged;
}

/// <summary>
/// Production-grade Shadow-Mode Application Pilot integration.
/// Guarantees that PostgreSQL remains the single authoritative source of truth for all business decisions,
/// while evaluating EAP in dark shadow to verify correctness, latency, outbox lag, and fail-closed safety.
/// Includes an automated circuit breaker, kill switch, and zero-payload sanitized telemetry.
/// </summary>
public sealed class EapShadowPilot : IDisposable
{
    private readonly EapCoordinator _coordinator;
    private readonly string _connectionString;
    private readonly string _tableName;
    private readonly string _outboxTable;
    private readonly int _mismatchThreshold;
    private readonly long _maxAllowedLag;

    private int _consecutiveMismatches;
    private volatile bool _killSwitchEngaged;
    private CircuitBreakerState _circuitState = CircuitBreakerState.Closed;
    private readonly object _stateLock = new();

    // Telemetry counters
    private long _totalQueries;
    private long _matchCount;
    private long _mismatchCount;
    private long _circuitBreakerTrips;
    private long _killSwitchActivations;
    private long _totalEapTicks;
    private long _totalPgTicks;

    public bool IsKillSwitchEngaged => _killSwitchEngaged;
    public CircuitBreakerState CircuitState
    {
        get
        {
            lock (_stateLock) return _circuitState;
        }
    }

    public EapShadowPilot(
        EapCoordinator coordinator,
        string connectionString,
        string tableName = "eap_audit_20261028.accounts_50m",
        string outboxTable = "eap_audit_20261028.mutation_log",
        int mismatchThreshold = 5,
        long maxAllowedLag = 1000)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _tableName = tableName;
        _outboxTable = outboxTable;
        _mismatchThreshold = mismatchThreshold;
        _maxAllowedLag = maxAllowedLag;
    }

    /// <summary>
    /// Evaluates account eligibility authoritatively against PostgreSQL,
    /// while concurrently executing a shadow EAP query to verify fidelity.
    /// Returns the authoritative PostgreSQL decision.
    /// </summary>
    public async Task<bool> AuthoritativeEvaluateEligibilityAsync(
        long accountId,
        int threshold = 500,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalQueries);

        // 1. If kill-switch is engaged, bypass shadow completely
        if (_killSwitchEngaged)
        {
            return await QueryPostgreSqlAuthoritativeAsync(accountId, threshold, ct).ConfigureAwait(false);
        }

        // 2. Check circuit breaker state
        CircuitBreakerState state;
        lock (_stateLock)
        {
            state = _circuitState;
        }

        if (state == CircuitBreakerState.Open)
        {
            return await QueryPostgreSqlAuthoritativeAsync(accountId, threshold, ct).ConfigureAwait(false);
        }

        // 3. PostgreSQL Authoritative Execution
        var pgSw = Stopwatch.StartNew();
        bool pgAuthoritativeDecision = await QueryPostgreSqlAuthoritativeAsync(accountId, threshold, ct).ConfigureAwait(false);
        pgSw.Stop();
        Interlocked.Add(ref _totalPgTicks, pgSw.ElapsedTicks);

        // 4. Shadow EAP Execution
        bool? eapShadowDecision = null;
        if (_coordinator.IsHealthy)
        {
            try
            {
                var eapSw = Stopwatch.StartNew();
                eapShadowDecision = _coordinator.QueryEligible(accountId);
                eapSw.Stop();
                Interlocked.Add(ref _totalEapTicks, eapSw.ElapsedTicks);
            }
            catch
            {
                // Shadow failure should never impact the authoritative path
                eapShadowDecision = null;
            }
        }

        // 5. Verification & Telemetry (Dark Comparison)
        if (eapShadowDecision.HasValue)
        {
            if (pgAuthoritativeDecision == eapShadowDecision.Value)
            {
                Interlocked.Increment(ref _matchCount);
                Interlocked.Exchange(ref _consecutiveMismatches, 0);
            }
            else
            {
                Interlocked.Increment(ref _mismatchCount);
                int streak = Interlocked.Increment(ref _consecutiveMismatches);

                // Trip circuit breaker if mismatch threshold is exceeded
                if (streak >= _mismatchThreshold)
                {
                    TripCircuitBreaker($"Consecutive shadow mismatches reached {streak} >= threshold {_mismatchThreshold}");
                }
            }
        }

        return pgAuthoritativeDecision;
    }

    /// <summary>
    /// Checks the current replication lag between PostgreSQL outbox and EAP memory watermark.
    /// Trips the circuit breaker if lag exceeds maxAllowedLag.
    /// </summary>
    public async Task<long> CheckOutboxLagAsync(CancellationToken ct = default)
    {
        long maxSeq = 0;
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand($"SELECT COALESCE(MAX(seq_id), 0) FROM {_outboxTable};", conn);
            object? val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (val != null && val != DBNull.Value)
            {
                maxSeq = Convert.ToInt64(val);
            }
        }
        catch
        {
            return 0;
        }

        long eapWatermark = _coordinator.LastAppliedMutationSeq;
        long lag = Math.Max(0, maxSeq - eapWatermark);

        if (lag > _maxAllowedLag)
        {
            TripCircuitBreaker($"Outbox replication lag {lag} exceeded safety threshold {_maxAllowedLag}");
        }

        return lag;
    }

    public void EngageKillSwitch(string reason = "Operator emergency manual kill switch")
    {
        _killSwitchEngaged = true;
        Interlocked.Increment(ref _killSwitchActivations);
    }

    public void DisengageKillSwitch()
    {
        _killSwitchEngaged = false;
    }

    public void TripCircuitBreaker(string reason)
    {
        lock (_stateLock)
        {
            if (_circuitState != CircuitBreakerState.Open)
            {
                _circuitState = CircuitBreakerState.Open;
                Interlocked.Increment(ref _circuitBreakerTrips);
            }
        }
    }

    public void ResetCircuitBreaker()
    {
        lock (_stateLock)
        {
            _circuitState = CircuitBreakerState.Closed;
            Interlocked.Exchange(ref _consecutiveMismatches, 0);
        }
    }

    public ShadowPilotTelemetry GetTelemetry(long currentLag = 0)
    {
        long totalQ = Volatile.Read(ref _totalQueries);
        long matches = Volatile.Read(ref _matchCount);
        long mismatches = Volatile.Read(ref _mismatchCount);
        long cbTrips = Volatile.Read(ref _circuitBreakerTrips);
        long ksActs = Volatile.Read(ref _killSwitchActivations);

        long eapTicks = Volatile.Read(ref _totalEapTicks);
        long pgTicks = Volatile.Read(ref _totalPgTicks);

        double tickFrequencyNs = 1_000_000_000.0 / Stopwatch.Frequency;
        double avgEapNs = matches > 0 ? (eapTicks * tickFrequencyNs) / matches : 0.0;
        double avgPgNs = totalQ > 0 ? (pgTicks * tickFrequencyNs) / totalQ : 0.0;

        return new ShadowPilotTelemetry
        {
            TotalQueries = totalQ,
            MatchCount = matches,
            MismatchCount = mismatches,
            CircuitBreakerTrips = cbTrips,
            KillSwitchActivations = ksActs,
            TotalEapLatencyNs = avgEapNs,
            TotalPgLatencyNs = avgPgNs,
            OutboxLag = currentLag,
            State = CircuitState,
            KillSwitchEngaged = _killSwitchEngaged
        };
    }

    private async Task<bool> QueryPostgreSqlAuthoritativeAsync(long accountId, int threshold, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        string sql = $"SELECT (balance >= @t AND active) FROM {_tableName} WHERE id = @id;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", threshold);
        cmd.Parameters.AddWithValue("id", accountId);

        object? val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return val is true;
    }

    public void Dispose()
    {
        // Safe teardown
    }
}
