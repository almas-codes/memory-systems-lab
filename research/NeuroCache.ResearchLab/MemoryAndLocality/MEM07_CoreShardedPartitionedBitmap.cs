using System;
using System.Runtime.CompilerServices;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

public sealed class BitmapShard
{
    public readonly EapBooleanBitmap Bitmap;
    public readonly object Lock = new();

    public BitmapShard(long capacity)
    {
        Bitmap = new EapBooleanBitmap(capacity);
    }
}

/// <summary>
/// MEM-07: Core-Sharded Partitioned Bitmap (CSPB)
/// Shards writeable ID space across 16 CPU-core-aligned partitions, eliminating global lock contention.
/// </summary>
public sealed class MEM07_CoreShardedPartitionedBitmap : IExperimentCandidate, IExactRecordLookup<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "MEM-CSPB-007";
    public string CandidateName => "Core-Sharded Partitioned Bitmap (CSPB / MEM-07)";
    public string Family => "MemoryAndLocality";
    public string Description => "Partitions ID space into 16 independent core-aligned shards with per-shard lock-free reads and isolated writes.";

    public const int ShardCount = 16;
    private readonly BitmapShard[] _shards;
    private readonly long _maxId;
    private readonly long _shardCapacity;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => ShardCount * _shards[0].Bitmap.RawMemoryBytes;
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => ShardCount * 64L;

    public MEM07_CoreShardedPartitionedBitmap(long maxId)
    {
        _maxId = maxId;
        _shardCapacity = (maxId + ShardCount - 1) / ShardCount;
        _shards = new BitmapShard[ShardCount];
        for (int i = 0; i < ShardCount; i++)
        {
            _shards[i] = new BitmapShard(_shardCapacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int Shard, long Offset) GetShardAddress(long id)
    {
        long zero = id - 1;
        int shard = (int)((ulong)zero % (ulong)ShardCount);
        long offset = (zero / ShardCount) + 1;
        return (shard, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        var (s, off) = GetShardAddress(id);
        return _shards[s].Bitmap.IsEligible(off);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetEligible(long id, bool eligible)
    {
        var (s, off) = GetShardAddress(id);
        lock (_shards[s].Lock)
        {
            _shards[s].Bitmap.SetEligible(off, eligible);
        }
    }
}
