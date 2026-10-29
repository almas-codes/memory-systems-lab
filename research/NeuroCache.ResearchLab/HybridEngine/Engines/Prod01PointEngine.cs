using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.Production;

namespace NeuroCache.ResearchLab.HybridEngine.Engines;

public sealed class Prod01PointEngine : IPointQueryEngine
{
    private PROD01_FourBitExceptionSeqlock _inner;
    private long _appliedVersion;
    private readonly long _maxId;

    public string EngineName => "PROD-01 (4-Bit Exception Seqlock)";
    public long AppliedVersion => Volatile.Read(ref _appliedVersion);
    public long MaxSupportedId => _maxId;
    public long TotalAllocatedBytes => _inner.TotalAllocatedBytes;
    public PROD01_FourBitExceptionSeqlock Inner => _inner;

    public Prod01PointEngine(long maxId, long initialVersion = 0)
    {
        _maxId = maxId;
        _inner = new PROD01_FourBitExceptionSeqlock(maxId);
        _appliedVersion = initialVersion;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SupportsPredicate(int predicateIndex) => predicateIndex >= 0 && predicateIndex < 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long accountId, int predicateIndex)
    {
        if (accountId < 0 || accountId > _maxId || predicateIndex < 0 || predicateIndex >= 8)
            return false;

        return _inner.EvaluatePredicate(accountId, predicateIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long accountId)
    {
        if (accountId < 0 || accountId > _maxId)
            return 0;

        return _inner.QueryAll(accountId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyMutation(long accountId, int balance, bool active, long commitSeq)
    {
        long curVer = Volatile.Read(ref _appliedVersion);
        if (commitSeq < curVer && curVer > 0)
        {
            // Stale event arrived out-of-order; drop to maintain causal consistency
            return;
        }

        _inner.HandleMutation(accountId, balance, active, commitSeq);
        if (commitSeq > curVer)
        {
            Volatile.Write(ref _appliedVersion, commitSeq);
        }
    }

    public void Rebuild(long maxId, IEnumerable<(long id, int balance, bool active)> records, long snapshotVersion)
    {
        var next = new PROD01_FourBitExceptionSeqlock(maxId);
        foreach (var (id, bal, act) in records)
        {
            next.HandleMutation(id, bal, act, snapshotVersion);
        }
        Interlocked.Exchange(ref _inner, next);
        Volatile.Write(ref _appliedVersion, snapshotVersion);
    }
}
