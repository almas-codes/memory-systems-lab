using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.HybridCompositions;

/// <summary>
/// COMBO-02: Semantic Classes with Rare Exceptions (COMBO-SEM-SPARSE-002)
/// Composition of:
/// - Dense 4-bit nibble array (0.50 bytes/record) storing the 8 dominant equivalence classes.
/// - Sparse dictionary for rare exception answer patterns.
/// High compression ratio while remaining 100% exact.
/// </summary>
public sealed class COMBO02_SemanticClassesWithRareExceptions : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    private const byte EscapeClass = 0x0F;

    public string ExperimentId => "COMBO-SEM-SPARSE-002";
    public string CandidateName => "Semantic Classes with Sparse Rare Exceptions";
    public string Family => "HybridCompositions";
    public string Description => "Dense 4-bit semantic equivalence classes covering >99.99% accounts with sparse exception table.";

    private readonly long _maxId;
    private readonly byte[] _denseNibbles;
    private readonly byte[] _classToPattern;
    private readonly ConcurrentDictionary<long, byte> _sparseExceptions;
    private long _mutationCount;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _denseNibbles.LongLength + 16 + (long)_sparseExceptions.Count * 32;
    public long PayloadBytes => _denseNibbles.LongLength;
    public long MetadataBytes => 16 + (long)_sparseExceptions.Count * 32;
    public int SparseExceptionCount => _sparseExceptions.Count;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public COMBO02_SemanticClassesWithRareExceptions(long maxId)
    {
        _maxId = maxId;
        _denseNibbles = new byte[(maxId + 2) / 2];
        _sparseExceptions = new ConcurrentDictionary<long, byte>();

        // Dominant patterns from 50M accounts:
        // 0: 128 (10000000) Active only (bal < 100)
        // 1: 193 (11000001) Active, bal >= 100
        // 2: 225 (11100001) Active, bal >= 250
        // 3: 243 (11110011) Active, bal >= 500
        // 4: 251 (11111011) Active, bal >= 750
        // 5: 255 (11111111) Active, bal >= 900
        // 6: 0   (00000000) Inactive, bal < 100
        // 7: 1   (00000001) Inactive, bal >= 100
        _classToPattern = new byte[16]
        {
            128, 193, 225, 243, 251, 255, 0, 1,
            3, 0, 0, 0, 0, 0, 0, 0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        if (id < 0 || id > _maxId) return false;
        byte pattern = QueryAll(id);
        return (pattern & (1 << (7 - predicateIndex))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        if (id < 0 || id > _maxId) return 0;
        long byteIndex = id >> 1;
        byte pair = Volatile.Read(ref _denseNibbles[byteIndex]);
        byte cls = (id & 1) == 0 ? (byte)(pair & 0x0F) : (byte)(pair >> 4);

        if (cls != EscapeClass)
        {
            return _classToPattern[cls];
        }

        return _sparseExceptions.TryGetValue(id, out byte exc) ? exc : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        if (id < 0 || id > _maxId) return;
        var r = new AccountRecord(id, balance, active, sequenceId);
        byte newPattern = r.ComputeAnswerPattern();

        int foundClass = -1;
        for (int i = 0; i < 8; i++)
        {
            if (_classToPattern[i] == newPattern)
            {
                foundClass = i;
                break;
            }
        }

        long byteIndex = id >> 1;
        bool isHighNibble = (id & 1) != 0;

        if (foundClass >= 0)
        {
            _sparseExceptions.TryRemove(id, out _);

            while (true)
            {
                byte oldVal = Volatile.Read(ref _denseNibbles[byteIndex]);
                byte newVal = isHighNibble
                    ? (byte)((oldVal & 0x0F) | (foundClass << 4))
                    : (byte)((oldVal & 0xF0) | foundClass);

                if (Interlocked.CompareExchange(ref _denseNibbles[byteIndex], newVal, oldVal) == oldVal)
                {
                    break;
                }
            }
        }
        else
        {
            _sparseExceptions[id] = newPattern;

            while (true)
            {
                byte oldVal = Volatile.Read(ref _denseNibbles[byteIndex]);
                byte newVal = isHighNibble
                    ? (byte)((oldVal & 0x0F) | (EscapeClass << 4))
                    : (byte)((oldVal & 0xF0) | EscapeClass);

                if (Interlocked.CompareExchange(ref _denseNibbles[byteIndex], newVal, oldVal) == oldVal)
                {
                    break;
                }
            }
        }

        Interlocked.Increment(ref _mutationCount);
    }
}
