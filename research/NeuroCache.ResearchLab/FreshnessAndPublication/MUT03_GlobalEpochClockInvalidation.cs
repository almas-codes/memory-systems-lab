using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.FreshnessAndPublication;

public readonly record struct EpochAnswerEntry(bool Answer, ulong Epoch);

/// <summary>
/// MUT-03: Global Epoch Clock Invalidation (GECI)
/// Uses a monotonic 64-bit epoch clock to mark cached domains fresh or invalid.
/// Eliminates per-record invalidation while preventing counter wraparound aliasing.
/// </summary>
public sealed class MUT03_GlobalEpochClockInvalidation : IExperimentCandidate, IExactRecordLookup<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "FRESH-GECI-003";
    public string CandidateName => "Global Epoch Clock Invalidation (GECI)";
    public string Family => "FreshnessAndPublication";
    public string Description => "Monotonic 64-bit domain epoch clock preventing 2-bit modulo-4 wraparound aliasing.";

    private ulong _globalEpoch = 1;
    private readonly EpochAnswerEntry[] _entries;
    private readonly Func<long, bool> _oracleRecompute;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public ulong CurrentEpoch => Volatile.Read(ref _globalEpoch);
    public long TotalAllocatedBytes => _entries.LongLength * 16L;
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 64;

    public MUT03_GlobalEpochClockInvalidation(long maxId, Func<long, bool> oracleRecompute)
    {
        _maxId = maxId;
        _oracleRecompute = oracleRecompute;
        _entries = new EpochAnswerEntry[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        ulong curEpoch = Volatile.Read(ref _globalEpoch);
        var entry = _entries[id];

        // Safe epoch check: must exactly match current monotonic epoch
        if (entry.Epoch == curEpoch)
        {
            return entry.Answer;
        }

        // Lazy recompute on epoch advance
        bool fresh = _oracleRecompute(id);
        _entries[id] = new EpochAnswerEntry(fresh, curEpoch);
        return fresh;
    }

    public void InvalidateDomain()
    {
        Interlocked.Increment(ref _globalEpoch);
    }

    /// <summary>
    /// Demonstrates why naive 2-bit modulo-4 aliasing fails:
    /// After 4 epoch ticks, a stale entry with tag 1 appears identical to epoch 5 (5 % 4 = 1).
    /// </summary>
    public static bool SimulateNaiveTwoBitAliasingFailure(byte cachedTag, byte newEpoch)
    {
        return (cachedTag & 3) == (newEpoch & 3);
    }
}
