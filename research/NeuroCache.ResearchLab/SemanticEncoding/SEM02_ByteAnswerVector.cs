using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.SemanticEncoding;

/// <summary>
/// SEM-02: Byte-per-Record Answer Vector
/// Stores the answers to eight Boolean predicates in one contiguous byte per record.
/// Footprint for 50M records: 50.00 MB decimal.
/// </summary>
public sealed class SEM02_ByteAnswerVector : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "SEM-BYTEVEC-002";
    public string CandidateName => "Byte-per-Record Answer Vector (SEM-02)";
    public string Family => "SemanticEncoding";
    public string Description => "Stores 8-bit answer vectors in a contiguous byte array (50 MB).";

    private readonly byte[] _patterns;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _patterns.LongLength;
    public long PayloadBytes => _patterns.LongLength;
    public long MetadataBytes => 64;

    public SEM02_ByteAnswerVector(long maxId)
    {
        _maxId = maxId;
        _patterns = new byte[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id) => _patterns[id];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex) => ((_patterns[id] >> (7 - predicateIndex)) & 1) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        var r = new AccountRecord(id, balance, active, sequenceId);
        _patterns[id] = r.ComputeAnswerPattern();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPatternDirect(long id, byte pattern) => _patterns[id] = pattern;
}
