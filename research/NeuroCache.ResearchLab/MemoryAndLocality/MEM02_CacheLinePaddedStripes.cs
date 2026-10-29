using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedSlot
{
    [FieldOffset(0)]
    public long Value;
}

/// <summary>
/// MEM-02: Cache-Line-Padded Stripes (CLPS)
/// Uses 64-byte aligned structs to eliminate CPU L1/L2 cache-line false sharing during concurrent thread writes.
/// </summary>
public sealed class MEM02_CacheLinePaddedStripes : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "MEM-CLPS-002";
    public string CandidateName => "Cache-Line-Padded Stripes (CLPS / MEM-02)";
    public string Family => "MemoryAndLocality";
    public string Description => "Partitions writeable state into 64-byte cache-line padded stripes to prevent CPU false sharing.";

    public const int StripeCount = 16;
    private readonly PaddedSlot[] _paddedStripes;
    private readonly long[] _unpaddedStripes;

    public long TotalAllocatedBytes => (_paddedStripes.Length * 64L) + (_unpaddedStripes.Length * 8L);
    public long PayloadBytes => StripeCount * 8L;
    public long MetadataBytes => (_paddedStripes.Length * 56L);

    public MEM02_CacheLinePaddedStripes()
    {
        _paddedStripes = new PaddedSlot[StripeCount];
        _unpaddedStripes = new long[StripeCount];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePadded(int stripeIndex, long val)
    {
        Interlocked.Exchange(ref _paddedStripes[stripeIndex].Value, val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnpadded(int stripeIndex, long val)
    {
        Interlocked.Exchange(ref _unpaddedStripes[stripeIndex], val);
    }
}
