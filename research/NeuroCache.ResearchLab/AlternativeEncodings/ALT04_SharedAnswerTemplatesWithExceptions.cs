using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.AlternativeEncodings;

/// <summary>
/// ALT-04: Shared Answer Templates with Sparse Exceptions
/// Maps records to default canonical templates (Template 0: Active Basic, Template 1: Inactive);
/// deviations are recorded as sparse exceptions.
/// </summary>
public sealed class ALT04_SharedAnswerTemplatesWithExceptions : IExperimentCandidate, IExactRecordLookup<long, byte>, IMemoryAccountable
{
    public string ExperimentId => "ALT-TEMPLATES-004";
    public string CandidateName => "Shared Answer Templates with Sparse Exceptions (ALT-04)";
    public string Family => "AlternativeEncodings";
    public string Description => "Maps records to canonical answer templates with sparse delta exceptions.";

    private static readonly byte[] Templates = new byte[]
    {
        0x80, // Template 0: Active, low balance
        0xC1, // Template 1: Active, balance 100-249
        0x00  // Template 2: Inactive
    };

    private readonly byte[] _templateIndices; // 2 bits per record (4 records/byte)
    private readonly ConcurrentDictionary<long, byte> _exceptions = new();
    private readonly long _maxId;

    public long MaxId => _maxId;
    public int ExceptionCount => _exceptions.Count;
    public long TotalAllocatedBytes => _templateIndices.LongLength + (_exceptions.Count * 24L);
    public long PayloadBytes => _templateIndices.LongLength;
    public long MetadataBytes => _exceptions.Count * 24L;

    public ALT04_SharedAnswerTemplatesWithExceptions(long maxId)
    {
        _maxId = maxId;
        _templateIndices = new byte[(maxId + 3) / 4];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        if (_exceptions.TryGetValue(id, out byte exc)) return exc;

        long byteIdx = id >> 2;
        int shift = (int)((id & 3) * 2);
        byte t = (byte)((_templateIndices[byteIdx] >> shift) & 3);
        return t < 3 ? Templates[t] : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAnswer(long id, byte pattern)
    {
        byte tIdx;
        if (pattern == Templates[0]) tIdx = 0;
        else if (pattern == Templates[1]) tIdx = 1;
        else if (pattern == Templates[2]) tIdx = 2;
        else
        {
            tIdx = 3; // Exception
            _exceptions[id] = pattern;
        }

        if (tIdx != 3) _exceptions.TryRemove(id, out _);

        long byteIdx = id >> 2;
        int shift = (int)((id & 3) * 2);
        ref byte target = ref _templateIndices[byteIdx];
        byte cur, next;
        do
        {
            cur = Volatile.Read(ref target);
            byte mask = (byte)(3 << shift);
            next = (byte)((cur & ~mask) | (tIdx << shift));
        } while (Interlocked.CompareExchange(ref target, next, cur) != cur);
    }
}
