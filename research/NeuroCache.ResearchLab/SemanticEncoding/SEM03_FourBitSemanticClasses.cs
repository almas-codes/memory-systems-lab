using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.SemanticEncoding;

/// <summary>
/// SEM-03: Four-Bit Semantic Class IDs
/// Packs 2 class IDs per byte (4 bits each, up to 16 classes) mapped to an in-memory class dictionary.
/// Footprint for 50M records: 25.00 MB decimal.
/// </summary>
public sealed class SEM03_FourBitSemanticClasses : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "SEM-CLASS4-003";
    public string CandidateName => "Four-Bit Semantic Class IDs (SEM-03)";
    public string Family => "SemanticEncoding";
    public string Description => "Packs 4-bit class IDs (2 per byte) with 16-slot dictionary and lock-free CAS updates.";

    private readonly byte[] _data;
    private readonly byte[] _classToPattern = new byte[16];
    private readonly byte[] _patternToClass = new byte[256];
    private readonly object _dictLock = new();
    private int _distinctClasses;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _data.LongLength + _classToPattern.Length + _patternToClass.Length;
    public long PayloadBytes => _data.LongLength;
    public long MetadataBytes => _classToPattern.Length + _patternToClass.Length;
    public int DistinctClassCount => Volatile.Read(ref _distinctClasses);

    public SEM03_FourBitSemanticClasses(long maxId)
    {
        _maxId = maxId;
        _data = new byte[(maxId + 2) / 2];
        Array.Fill(_patternToClass, (byte)0xFF);
        RegisterPattern(0);
    }

    public byte GetOrRegisterPattern(byte pattern)
    {
        byte existing = Volatile.Read(ref _patternToClass[pattern]);
        if (existing != 0xFF) return existing;

        lock (_dictLock)
        {
            if (_patternToClass[pattern] != 0xFF) return _patternToClass[pattern];
            if (_distinctClasses >= 16) throw new InvalidOperationException("4-bit class dictionary overflow.");
            byte classId = (byte)_distinctClasses;
            _classToPattern[classId] = pattern;
            _patternToClass[pattern] = classId;
            Interlocked.Increment(ref _distinctClasses);
            return classId;
        }
    }

    private void RegisterPattern(byte pattern)
    {
        byte classId = (byte)_distinctClasses;
        _classToPattern[classId] = pattern;
        _patternToClass[pattern] = classId;
        _distinctClasses++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        long byteIdx = id >> 1;
        byte b = _data[byteIdx];
        byte classId = (id & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
        return _classToPattern[classId];
    }

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
        byte classId = GetOrRegisterPattern(pat);

        long byteIdx = id >> 1;
        bool isHigh = (id & 1) == 0;
        ref byte target = ref _data[byteIdx];
        byte cur, next;
        do
        {
            cur = Volatile.Read(ref target);
            if (isHigh) next = (byte)((cur & 0x0F) | (classId << 4));
            else next = (byte)((cur & 0xF0) | (classId & 0x0F));
        } while (Interlocked.CompareExchange(ref target, next, cur) != cur);
    }
}
