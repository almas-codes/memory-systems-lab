using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.Recovery;

/// <summary>
/// REC-01: Copy-on-Write Answer Pages (REC-COW-001)
/// Divides answer storage into 4096-account pages.
/// Mutations duplicate the affected page, update the answer pattern, and atomically swap
/// the page reference into the root page directory.
/// Readers read stable immutable pages locklessly.
/// </summary>
public sealed class REC01_CopyOnWriteAnswerPages : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public const int PageSize = 4096;
    public const int PageShift = 12; // 2^12 = 4096
    public const int PageMask = PageSize - 1;

    public string ExperimentId => "REC-COW-001";
    public string CandidateName => "Copy-On-Write Answer Pages (REC-01)";
    public string Family => "Recovery";
    public string Description => "Partitions answer bytes into 4KB pages; mutations copy-on-write and atomic pointer swap.";

    private readonly long _maxId;
    private readonly int _pageCount;
    private readonly byte[][] _pageDirectory;
    private long _mutationCount;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => (long)_pageCount * IntPtr.Size + (long)_pageCount * PageSize;
    public long PayloadBytes => (long)_pageCount * PageSize;
    public long MetadataBytes => (long)_pageCount * IntPtr.Size;
    public long MutationCount => Volatile.Read(ref _mutationCount);

    public REC01_CopyOnWriteAnswerPages(long maxId)
    {
        _maxId = maxId;
        _pageCount = (int)((maxId + PageSize) / PageSize);
        _pageDirectory = new byte[_pageCount][];

        for (int p = 0; p < _pageCount; p++)
        {
            _pageDirectory[p] = new byte[PageSize];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        if (id < 0 || id > _maxId) return false;
        int pageIndex = (int)(id >> PageShift);
        int offset = (int)(id & PageMask);

        byte[] page = Volatile.Read(ref _pageDirectory[pageIndex]);
        byte pattern = page[offset];
        return (pattern & (1 << (7 - predicateIndex))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        if (id < 0 || id > _maxId) return 0;
        int pageIndex = (int)(id >> PageShift);
        int offset = (int)(id & PageMask);

        byte[] page = Volatile.Read(ref _pageDirectory[pageIndex]);
        return page[offset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        if (id < 0 || id > _maxId) return;
        int pageIndex = (int)(id >> PageShift);
        int offset = (int)(id & PageMask);

        var r = new AccountRecord(id, balance, active, sequenceId);
        byte newPattern = r.ComputeAnswerPattern();

        while (true)
        {
            byte[] oldPage = Volatile.Read(ref _pageDirectory[pageIndex]);
            if (oldPage[offset] == newPattern)
            {
                break;
            }

            byte[] newPage = new byte[PageSize];
            Array.Copy(oldPage, newPage, PageSize);
            newPage[offset] = newPattern;

            if (Interlocked.CompareExchange(ref _pageDirectory[pageIndex], newPage, oldPage) == oldPage)
            {
                Interlocked.Increment(ref _mutationCount);
                break;
            }
        }
    }
}
