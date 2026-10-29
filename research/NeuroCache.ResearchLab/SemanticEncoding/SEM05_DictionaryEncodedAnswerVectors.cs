using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.SemanticEncoding;

/// <summary>
/// SEM-05: Dictionary-Encoded Answer Vectors
/// Stores arbitrary dictionary mappings with 8-bit or 16-bit dictionary keys.
/// </summary>
public sealed class SEM05_DictionaryEncodedAnswerVectors : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "SEM-DICT-005";
    public string CandidateName => "Dictionary-Encoded Answer Vectors (SEM-05)";
    public string Family => "SemanticEncoding";
    public string Description => "Full dictionary encoding supporting up to 256 distinct answer vectors with dynamic registry.";

    private readonly byte[] _classIds;
    private readonly byte[] _dictionary = new byte[256];
    private readonly ConcurrentDictionary<byte, byte> _reverseDict = new();
    private int _classCount;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _classIds.LongLength + _dictionary.Length + (_reverseDict.Count * 32L);
    public long PayloadBytes => _classIds.LongLength;
    public long MetadataBytes => _dictionary.Length + (_reverseDict.Count * 32L);
    public int DistinctClassCount => Volatile.Read(ref _classCount);

    public SEM05_DictionaryEncodedAnswerVectors(long maxId)
    {
        _maxId = maxId;
        _classIds = new byte[maxId + 1];
        GetOrAddClass(0);
    }

    public byte GetOrAddClass(byte pattern)
    {
        return _reverseDict.GetOrAdd(pattern, p =>
        {
            int idx = Interlocked.Increment(ref _classCount) - 1;
            if (idx >= 256) throw new InvalidOperationException("Dictionary overflow.");
            byte c = (byte)idx;
            _dictionary[c] = p;
            return c;
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id) => _dictionary[_classIds[id]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        byte pat = Query(id);
        return ((pat >> (7 - predicateIndex)) & 1) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        var r = new AccountRecord(id, balance, active, sequenceId);
        byte pat = r.ComputeAnswerPattern();
        byte c = GetOrAddClass(pat);
        _classIds[id] = c;
    }
}
