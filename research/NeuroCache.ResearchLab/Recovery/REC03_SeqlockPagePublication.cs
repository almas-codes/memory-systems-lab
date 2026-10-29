using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.Recovery;

/// <summary>
/// REC-03: Seqlock Page Publication (REC-SEQLOCK-003)
/// Partitions accounts into pages protected by a 64-bit sequence counter (seqlock).
/// Writers increment the page sequence to an odd number before updating, and to an even number after.
/// Readers spin and retry if sequence was odd or changed during the read.
/// </summary>
public sealed class REC03_SeqlockPagePublication : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public const int PageSize = 1024;
    public const int PageShift = 10;
    public const int PageMask = PageSize - 1;

    public string ExperimentId => "REC-SEQLOCK-003";
    public string CandidateName => "Seqlock Page Publication (REC-03)";
    public string Family => "Recovery";
    public string Description => "Partitions memory into 1024-account pages with 64-bit seqlocks for tear-free cross-predicate consistency.";

    private readonly long _maxId;
    private readonly int _pageCount;
    private readonly byte[] _answers;
    private readonly long[] _pageSequences;
    private readonly object[] _pageLocks;
    private long _mutationCount;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => (_maxId + 1) + (long)_pageCount * sizeof(long) + (long)_pageCount * IntPtr.Size;
    public long PayloadBytes => _maxId + 1;
    public long MetadataBytes => (long)_pageCount * (sizeof(long) + IntPtr.Size);
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public REC03_SeqlockPagePublication(long maxId)
    {
        _maxId = maxId;
        _pageCount = (int)((maxId + PageSize) / PageSize);
        _answers = new byte[maxId + 1];
        _pageSequences = new long[_pageCount];
        _pageLocks = new object[_pageCount];
        for (int p = 0; p < _pageCount; p++)
        {
            _pageLocks[p] = new object();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        if (id < 0 || id > _maxId) return false;
        int pageIndex = (int)(id >> PageShift);

        var spinner = new SpinWait();
        while (true)
        {
            long seqBefore = Volatile.Read(ref _pageSequences[pageIndex]);
            if ((seqBefore & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            byte pattern = Volatile.Read(ref _answers[id]);

            long seqAfter = Volatile.Read(ref _pageSequences[pageIndex]);
            if (seqBefore == seqAfter)
            {
                return (pattern & (1 << (7 - predicateIndex))) != 0;
            }

            spinner.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        if (id < 0 || id > _maxId) return 0;
        int pageIndex = (int)(id >> PageShift);

        var spinner = new SpinWait();
        while (true)
        {
            long seqBefore = Volatile.Read(ref _pageSequences[pageIndex]);
            if ((seqBefore & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            byte pattern = Volatile.Read(ref _answers[id]);

            long seqAfter = Volatile.Read(ref _pageSequences[pageIndex]);
            if (seqBefore == seqAfter)
            {
                return pattern;
            }

            spinner.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        if (id < 0 || id > _maxId) return;
        int pageIndex = (int)(id >> PageShift);
        var r = new AccountRecord(id, balance, active, sequenceId);
        byte newPattern = r.ComputeAnswerPattern();

        lock (_pageLocks[pageIndex])
        {
            ref long pageSeq = ref _pageSequences[pageIndex];
            long currentSeq = Volatile.Read(ref pageSeq);
            Volatile.Write(ref pageSeq, currentSeq + 1);

            Thread.MemoryBarrier();

            Volatile.Write(ref _answers[id], newPattern);

            Thread.MemoryBarrier();

            Volatile.Write(ref pageSeq, currentSeq + 2);
            Interlocked.Increment(ref _mutationCount);
        }
    }
}
