using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

/// <summary>
/// MEM-08: Access-Weighted Block Layout
/// Arranges memory blocks in frequency order so frequently queried blocks occupy contiguous low-cache memory.
/// </summary>
public sealed class MEM08_AccessWeightedBlockLayout : IExperimentCandidate, IExactRecordLookup<long, byte>, IMemoryAccountable
{
    public string ExperimentId => "MEM-BLOCKLAYOUT-008";
    public string CandidateName => "Access-Weighted Block Layout (MEM-08)";
    public string Family => "MemoryAndLocality";
    public string Description => "Sorts physical blocks in observed frequency order to maximize L2/L3 prefetcher locality.";

    public const int BlockSize = 1024;
    private readonly int[] _logicalToPhysicalBlockMap;
    private readonly byte[] _physicalStorage;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _physicalStorage.LongLength + (_logicalToPhysicalBlockMap.Length * 4L);
    public long PayloadBytes => _physicalStorage.LongLength;
    public long MetadataBytes => _logicalToPhysicalBlockMap.Length * 4L;

    public MEM08_AccessWeightedBlockLayout(long maxId, int[] frequencyRankedBlocks)
    {
        _maxId = maxId;
        int blockCount = (int)((maxId + BlockSize - 1) / BlockSize);
        _logicalToPhysicalBlockMap = new int[blockCount];
        _physicalStorage = new byte[(long)blockCount * BlockSize];

        for (int physicalSlot = 0; physicalSlot < frequencyRankedBlocks.Length && physicalSlot < blockCount; physicalSlot++)
        {
            int logicalBlock = frequencyRankedBlocks[physicalSlot];
            _logicalToPhysicalBlockMap[logicalBlock] = physicalSlot;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        long zero = id - 1;
        int logicalBlock = (int)(zero / BlockSize);
        int offset = (int)(zero % BlockSize);

        int physicalBlock = _logicalToPhysicalBlockMap[logicalBlock];
        long physicalAddress = (long)physicalBlock * BlockSize + offset;
        return _physicalStorage[physicalAddress];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAnswer(long id, byte pattern)
    {
        long zero = id - 1;
        int logicalBlock = (int)(zero / BlockSize);
        int offset = (int)(zero % BlockSize);

        int physicalBlock = _logicalToPhysicalBlockMap[logicalBlock];
        long physicalAddress = (long)physicalBlock * BlockSize + offset;
        _physicalStorage[physicalAddress] = pattern;
    }
}
