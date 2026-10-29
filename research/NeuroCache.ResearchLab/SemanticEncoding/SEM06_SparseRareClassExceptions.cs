using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.SemanticEncoding;

/// <summary>
/// SEM-06: Sparse Rare-Class Exceptions
/// Encodes common classes in a compact 2-bit plane (4 records per byte = 12.5 MB / 50M).
/// Code 0b11 routes to a sparse exception index for uncommon classes.
/// </summary>
public sealed class SEM06_SparseRareClassExceptions : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "SEM-SPARSE-006";
    public string CandidateName => "Sparse Rare-Class Exceptions (SEM-06)";
    public string Family => "SemanticEncoding";
    public string Description => "Packs top 3 classes into 2-bit plane (12.5 MB / 50M); rare patterns route to sparse exception dictionary.";

    // 2-bit plane: 4 records per byte
    // 0b00: Pattern 0x80 (Active, bal < 100)
    // 0b01: Pattern 0xC1 (Active, 100 <= bal < 250)
    // 0b10: Pattern 0x00 (Inactive, bal < 100)
    // 0b11: Exception (Look up in _exceptions)
    public const byte EscapeCode = 0x03;

    private static readonly byte[] CommonPatterns = new byte[] { 0x80, 0xC1, 0x00 };
    private readonly byte[] _plane;
    private readonly ConcurrentDictionary<long, byte> _exceptions = new();
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _plane.LongLength + (_exceptions.Count * 24L);
    public long PayloadBytes => _plane.LongLength;
    public long MetadataBytes => _exceptions.Count * 24L;
    public int ExceptionCount => _exceptions.Count;

    public SEM06_SparseRareClassExceptions(long maxId)
    {
        _maxId = maxId;
        _plane = new byte[(maxId + 4) / 4];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        long byteIdx = id >> 2;
        int shift = (int)((id & 3) * 2);
        byte code = (byte)((_plane[byteIdx] >> shift) & 0x03);

        if (code < 3)
        {
            return CommonPatterns[code];
        }

        return _exceptions.TryGetValue(id, out byte pat) ? pat : (byte)0;
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

        byte code;
        if (pat == 0x80) code = 0;
        else if (pat == 0xC1) code = 1;
        else if (pat == 0x00) code = 2;
        else
        {
            code = EscapeCode;
            _exceptions[id] = pat;
        }

        if (code != EscapeCode)
        {
            _exceptions.TryRemove(id, out _);
        }

        long byteIdx = id >> 2;
        int shift = (int)((id & 3) * 2);
        ref byte target = ref _plane[byteIdx];
        byte cur, next;
        do
        {
            cur = Volatile.Read(ref target);
            byte mask = (byte)(0x03 << shift);
            next = (byte)((cur & ~mask) | (code << shift));
        } while (Interlocked.CompareExchange(ref target, next, cur) != cur);
    }
}
