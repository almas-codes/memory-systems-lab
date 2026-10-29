using System;
using System.Collections.Generic;
using NeuroCache.ResearchLab.HybridEngine.Contracts;

namespace NeuroCache.ResearchLab.ShadowPilot.Contracts;

public enum ShadowPilotMode
{
    Mode1_OfflineDeterministicReplay = 1,
    Mode2_StagingIsolatedDatabase = 2,
    Mode3_LiveShadow = 3 // Disabled by default, requires explicit authorization
}

public enum ShadowComparisonOutcome
{
    ExactMatch = 1,
    VersionSkewMismatch = 2,     // Discrepancy explained by distinct source version/watermark
    PredicateMismatch = 3,       // True correctness failure on identical version
    ShadowTimeout = 4,           // Hybrid exceeded configured shadow time budget
    ShadowError = 5,             // Isolated exception within hybrid execution
    SkippedCircuitBreaker = 6,   // Skipped because safety breaker is open
    SkippedRateLimit = 7,        // Skipped to respect resource budget
    UnsupportedQuery = 8         // Query outside declared family; routed solely to authoritative
}

public sealed record ShadowRequestRecord(
    long RequestId,
    DateTime TimestampUtc,
    RequestType Type,
    long AccountId,
    int PredicateIndex,
    AnalyticalQuery? Analytical,
    long RequiredVersion,
    ConsistencyMode Consistency,
    long MaxAccountId);

public sealed record ShadowComparisonRecord(
    long RequestId,
    ShadowComparisonOutcome Outcome,
    object? AuthoritativeResult,
    object? ShadowResult,
    double AuthoritativeLatencyNs,
    double ShadowLatencyNs,
    long AuthoritativeVersion,
    long ShadowVersion,
    string? Notes = null);

public sealed record ShadowCircuitBreakerConfig(
    int MaxConsecutiveMismatches = 5,
    double MaxMismatchRatePercent = 0.5,
    int MaxSourceLagEvents = 10_000,
    int MaxDatabaseTimeouts = 10,
    int MaxConcurrentAnalyticalQueries = 8,
    double MaxShadowQueryRatePerSec = 1_000_000,
    int ShadowQueryTimeoutMs = 100);

public sealed record ShadowRunManifest(
    string ManifestId,
    ShadowPilotMode Mode,
    string DatasetName,
    long RecordCount,
    int RandomSeed,
    string GitCommitHash,
    ShadowCircuitBreakerConfig Config,
    DateTime StartTimeUtc,
    DateTime? EndTimeUtc = null,
    string? Notes = null);
