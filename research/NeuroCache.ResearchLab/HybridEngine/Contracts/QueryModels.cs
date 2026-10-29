using System;
using System.Collections.Generic;

namespace NeuroCache.ResearchLab.HybridEngine.Contracts;

public enum RequestType
{
    PointPredicate = 1,
    PointAll = 2,
    AnalyticalCount = 3,
    AnalyticalIds = 4,
    AnalyticalBatch = 5,
    CustomSql = 6
}

public enum ConsistencyMode
{
    SourceCurrent = 1,      // Must reflect authoritative source; waits or falls back
    ReadYourWrites = 2,     // Guarantees caller observes their own write
    BoundedStaleness = 3    // Tolerates lag up to configured limit
}

public enum RoutedEngine
{
    Unknown = 0,
    Prod01Point = 1,
    Thr02Analytical = 2,
    ExactFallbackPostgres = 3,
    ExactFallbackInMemory = 4
}

public sealed record AnalyticalQuery(
    int Threshold,
    bool RequireActive = true,
    int[]? BatchThresholds = null,
    int Limit = 100);

public sealed record QueryRequest(
    RequestType Type,
    long AccountId = 0,
    int PredicateIndex = 0,
    AnalyticalQuery? Analytical = null,
    string? CustomSql = null,
    ConsistencyMode Consistency = ConsistencyMode.SourceCurrent,
    long RequiredVersion = 0,
    long MaxStalenessMs = 500,
    long MaxAccountId = 0);

public sealed record QueryResult(
    bool Success,
    RoutedEngine RoutedTo,
    bool BooleanResult = false,
    byte AnswerPattern = 0,
    long CountResult = 0,
    IReadOnlyList<long>? MatchingIds = null,
    long[]? BatchResults = null,
    long SourceVersion = 0,
    double ExecutionTimeNs = 0,
    bool IsStale = false,
    string? ErrorMessage = null)
{
    public static QueryResult CreatePoint(bool result, RoutedEngine engine, long version, double ns) =>
        new(Success: true, RoutedTo: engine, BooleanResult: result, SourceVersion: version, ExecutionTimeNs: ns);

    public static QueryResult CreatePointAll(byte pattern, RoutedEngine engine, long version, double ns) =>
        new(Success: true, RoutedTo: engine, AnswerPattern: pattern, SourceVersion: version, ExecutionTimeNs: ns);

    public static QueryResult CreateAnalyticalCount(long count, RoutedEngine engine, long version, double ns) =>
        new(Success: true, RoutedTo: engine, CountResult: count, SourceVersion: version, ExecutionTimeNs: ns);

    public static QueryResult CreateAnalyticalIds(IReadOnlyList<long> ids, RoutedEngine engine, long version, double ns) =>
        new(Success: true, RoutedTo: engine, MatchingIds: ids, CountResult: ids.Count, SourceVersion: version, ExecutionTimeNs: ns);

    public static QueryResult CreateAnalyticalBatch(long[] counts, RoutedEngine engine, long version, double ns) =>
        new(Success: true, RoutedTo: engine, BatchResults: counts, SourceVersion: version, ExecutionTimeNs: ns);

    public static QueryResult CreateFailure(string error, RoutedEngine engine = RoutedEngine.Unknown) =>
        new(Success: false, RoutedTo: engine, ErrorMessage: error);
}
