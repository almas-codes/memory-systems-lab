using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.SemanticEncoding;

/// <summary>
/// SEM-04: Three-Bit Semantic Class IDs
/// Genuinely packed 3-bit array crossing byte boundaries (8 distinct classes).
/// Footprint for 50M records: 18.75 MB decimal (17.88 MiB binary).
/// </summary>
public sealed class SEM04_ThreeBitSemanticClasses : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "SEM-CLASS3-004";
    public string CandidateName => "Three-Bit Semantic Class IDs (SEM-04)";
    public string Family => "SemanticEncoding";
    public string Description => "Genuinely packed 3-bit array crossing byte boundaries (18.75 MB / 50M).";

    private readonly byte[] _data;
    private readonly byte[] _classToPattern = new byte[8];
    private readonly byte[] _patternToClass = new byte[256];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _fallbackExceptions = new();
    private readonly object _dictLock = new();
    private readonly object[] _stripeLocks;
    private const int StripeCount = 64;
    private int _distinctClasses;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _data.LongLength + _classToPattern.Length + _patternToClass.Length + (long)_fallbackExceptions.Count * 32;
    public long PayloadBytes => _data.LongLength;
    public long MetadataBytes => _classToPattern.Length + _patternToClass.Length + (StripeCount * 24L) + (long)_fallbackExceptions.Count * 32;
    public int DistinctClassCount => Volatile.Read(ref _distinctClasses);

    public SEM04_ThreeBitSemanticClasses(long maxId)
    {
        _maxId = maxId;
        long totalBits = (maxId + 1) * 3L;
        _data = new byte[(totalBits + 7) / 8 + 1]; // +1 for boundary overflow safety
        Array.Fill(_patternToClass, (byte)0xFF);

        _stripeLocks = new object[StripeCount];
        for (int i = 0; i < StripeCount; i++) _stripeLocks[i] = new object();

        RegisterPattern(0);
    }

    public byte GetOrRegisterPattern(byte pattern)
    {
        byte existing = Volatile.Read(ref _patternToClass[pattern]);
        if (existing != 0xFF) return existing;

        lock (_dictLock)
        {
            if (_patternToClass[pattern] != 0xFF) return _patternToClass[pattern];
            if (_distinctClasses >= 7)
            {
                // Class 7 is the escape class for overflow patterns
                _classToPattern[7] = pattern;
                _patternToClass[pattern] = 7;
                return 7;
            }
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
    public byte ReadClassId(long id)
    {
        long bitPos = id * 3L;
        long byteIdx = bitPos >> 3;
        int bitOffset = (int)(bitPos & 7);

        if (bitOffset <= 5)
        {
            return (byte)((_data[byteIdx] >> bitOffset) & 0x07);
        }
        else
        {
            // Spans two bytes
            int bitsInFirst = 8 - bitOffset;
            int bitsInSecond = 3 - bitsInFirst;
            byte low = (byte)((_data[byteIdx] >> bitOffset) & ((1 << bitsInFirst) - 1));
            byte high = (byte)(_data[byteIdx + 1] & ((1 << bitsInSecond) - 1));
            return (byte)(low | (high << bitsInFirst));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteClassId(long id, byte classId)
    {
        long bitPos = id * 3L;
        long byteIdx = bitPos >> 3;
        int bitOffset = (int)(bitPos & 7);

        int stripe = (int)((ulong)byteIdx % StripeCount);
        lock (_stripeLocks[stripe])
        {
            if (bitOffset <= 5)
            {
                byte mask = (byte)(0x07 << bitOffset);
                _data[byteIdx] = (byte)((_data[byteIdx] & ~mask) | ((classId & 0x07) << bitOffset));
            }
            else
            {
                int bitsInFirst = 8 - bitOffset;
                int bitsInSecond = 3 - bitsInFirst;

                byte maskFirst = (byte)(((1 << bitsInFirst) - 1) << bitOffset);
                _data[byteIdx] = (byte)((_data[byteIdx] & ~maskFirst) | ((classId & ((1 << bitsInFirst) - 1)) << bitOffset));

                byte maskSecond = (byte)((1 << bitsInSecond) - 1);
                _data[byteIdx + 1] = (byte)((_data[byteIdx + 1] & ~maskSecond) | ((classId >> bitsInFirst) & maskSecond));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        byte classId = ReadClassId(id);
        if (classId == 7 && _fallbackExceptions.TryGetValue(id, out byte exc))
        {
            return exc;
        }
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
        WriteClassId(id, classId);

        if (classId == 7)
        {
            _fallbackExceptions[id] = pat;
        }
        else
        {
            _fallbackExceptions.TryRemove(id, out _);
        }
    }
}
