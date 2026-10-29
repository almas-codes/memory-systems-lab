using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.Representations;

/// <summary>
/// REP-01: Correlated-Outcome Joint Bitmap (CJB)
/// Represents joint answer pattern for a declared group of queries in a single packed byte per record.
/// </summary>
public sealed class REP01_CorrelatedOutcomeJointBitmap : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "REP-CJB-001";
    public string CandidateName => "Correlated-Outcome Joint Bitmap (CJB)";
    public string Family => "Representations";
    public string Description => "Stores joint 8-query answer patterns as packed bytes with direct bit extraction.";

    private readonly byte[] _patterns;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _patterns.LongLength;
    public long PayloadBytes => _patterns.LongLength;
    public long MetadataBytes => 64;

    public REP01_CorrelatedOutcomeJointBitmap(long maxId)
    {
        _maxId = maxId;
        _patterns = new byte[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id) => _patterns[id];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        byte pat = _patterns[id];
        return ((pat >> (7 - predicateIndex)) & 1) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        var record = new AccountRecord(id, balance, active, sequenceId);
        _patterns[id] = record.ComputeAnswerPattern();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPatternDirect(long id, byte pattern) => _patterns[id] = pattern;
}
