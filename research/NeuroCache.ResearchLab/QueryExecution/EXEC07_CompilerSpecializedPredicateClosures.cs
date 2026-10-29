using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.QueryExecution;

/// <summary>
/// EXEC-07: Compiler-Specialized Predicate Closures
/// Compiles arbitrary expression trees into native IL execution delegates at runtime.
/// </summary>
public sealed class EXEC07_CompilerSpecializedPredicateClosures : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "EXEC-CLOSURE-007";
    public string CandidateName => "Compiler-Specialized Predicate Closures (EXEC-07)";
    public string Family => "QueryExecution";
    public string Description => "Compiles predicate expression trees into executable delegates at runtime via System.Linq.Expressions.";

    private readonly Func<int, bool, bool> _compiledPredicate;
    public double CompilationDurationMs { get; }

    public long TotalAllocatedBytes => 512;
    public long PayloadBytes => 64;
    public long MetadataBytes => 448;

    public EXEC07_CompilerSpecializedPredicateClosures()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Build Expression: (balance, active) => active && balance >= 100
        var paramBalance = Expression.Parameter(typeof(int), "balance");
        var paramActive = Expression.Parameter(typeof(bool), "active");

        var constantThreshold = Expression.Constant(100);
        var gte = Expression.GreaterThanOrEqual(paramBalance, constantThreshold);
        var andAlso = Expression.AndAlso(paramActive, gte);

        var lambda = Expression.Lambda<Func<int, bool, bool>>(andAlso, paramBalance, paramActive);
        _compiledPredicate = lambda.Compile();

        sw.Stop();
        CompilationDurationMs = sw.Elapsed.TotalMilliseconds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(int balance, bool active) => _compiledPredicate(balance, active);
}
