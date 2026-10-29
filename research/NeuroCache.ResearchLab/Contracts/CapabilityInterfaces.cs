using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroCache.ResearchLab.Contracts;

public interface IExperimentCandidate
{
    string ExperimentId { get; }
    string CandidateName { get; }
    string Family { get; }
    string Description { get; }
}

public interface IExactRecordLookup<TKey, TResult>
{
    TResult Query(TKey id);
}

public interface IExactBatchLookup<TKey, TResult>
{
    void QueryBatch(ReadOnlySpan<TKey> ids, Span<TResult> destination);
}

public interface IPredicateEvaluator<TKey>
{
    bool EvaluatePredicate(TKey id, int predicateIndex);
}

public interface ISourceMutationHandler
{
    void HandleMutation(long id, int balance, bool active, long sequenceId);
}

public interface IRepresentationRebuild
{
    Task RebuildAsync(CancellationToken ct = default);
}

public interface ICompactionSupport
{
    bool NeedsCompaction { get; }
    void Compact();
}

public interface IGenerationPublisher
{
    long ActiveGeneration { get; }
    void PublishGeneration();
}

public interface IColdFallback<TKey, TResult>
{
    Task<TResult> FallbackQueryAsync(TKey id, CancellationToken ct = default);
}

public interface IRecoveryReplay
{
    Task ReplayFromSequenceAsync(long fromSeq, CancellationToken ct = default);
}

public interface IMemoryAccountable
{
    long TotalAllocatedBytes { get; }
    long PayloadBytes { get; }
    long MetadataBytes { get; }
    double DecimalMb => TotalAllocatedBytes / 1_000_000.0;
    double BinaryMib => TotalAllocatedBytes / (1024.0 * 1024.0);
}

public interface IDiagnosticCounters
{
    IReadOnlyDictionary<string, long> GetDiagnosticCounters();
}
