using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.Representations;

public enum BlockCodecType : byte
{
    AllFalse = 0,
    AllTrue = 1,
    SparseArray = 2,
    DenseBitmap = 3,
    RunLength = 4
}

public sealed class EbcBlock
{
    public BlockCodecType CodecType;
    public byte[]? Payload;
    public ulong[]? DenseWords;
    public ushort[]? SparseOffsets;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(int offsetInBlock) => CodecType switch
    {
        BlockCodecType.AllFalse => false,
        BlockCodecType.AllTrue => true,
        BlockCodecType.DenseBitmap => (DenseWords![offsetInBlock >> 6] & (1UL << (offsetInBlock & 63))) != 0,
        BlockCodecType.SparseArray => Array.BinarySearch(SparseOffsets!, (ushort)offsetInBlock) >= 0,
        BlockCodecType.RunLength => EvaluateRunLength(offsetInBlock),
        _ => false
    };

    private bool EvaluateRunLength(int offsetInBlock)
    {
        int cumulative = 0;
        bool state = false;
        for (int i = 0; i < Payload!.Length; i++)
        {
            cumulative += Payload[i];
            if (offsetInBlock < cumulative) return state;
            state = !state;
        }
        return false;
    }
}

/// <summary>
/// REP-05: Entropy/Cardinality-Driven Block Codec (EBC)
/// Selects cheapest exact encoding format per 512-bit block: AllFalse, AllTrue, SparseArray, DenseBitmap, or RunLength.
/// </summary>
public sealed class REP05_EntropyBlockCodec : IExperimentCandidate, IExactRecordLookup<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "REP-EBC-005";
    public string CandidateName => "Entropy/Cardinality-Driven Block Codec (EBC)";
    public string Family => "Representations";
    public string Description => "Chooses cheapest exact block format (AllFalse, AllTrue, Sparse, Dense, or RLE) per 512-record block.";

    public const int BlockSize = 512;
    private readonly EbcBlock[] _blocks;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes { get; private set; }
    public long PayloadBytes { get; private set; }
    public long MetadataBytes => _blocks.Length * 16;

    public REP05_EntropyBlockCodec(long maxId, Func<long, bool> predicateSupplier)
    {
        _maxId = maxId;
        int blockCount = (int)((maxId + BlockSize - 1) / BlockSize);
        _blocks = new EbcBlock[blockCount];
        long payloadBytes = 0;

        for (int b = 0; b < blockCount; b++)
        {
            long startId = (long)b * BlockSize + 1;
            int countInBlock = (int)Math.Min(BlockSize, maxId - startId + 1);

            int setBits = 0;
            var sparseList = new List<ushort>();
            var dense = new ulong[(countInBlock + 63) / 64];

            for (int i = 0; i < countInBlock; i++)
            {
                if (predicateSupplier(startId + i))
                {
                    setBits++;
                    sparseList.Add((ushort)i);
                    dense[i >> 6] |= (1UL << (i & 63));
                }
            }

            var block = new EbcBlock();
            if (setBits == 0)
            {
                block.CodecType = BlockCodecType.AllFalse;
            }
            else if (setBits == countInBlock)
            {
                block.CodecType = BlockCodecType.AllTrue;
            }
            else if (setBits <= 16)
            {
                block.CodecType = BlockCodecType.SparseArray;
                block.SparseOffsets = sparseList.ToArray();
                payloadBytes += block.SparseOffsets.Length * 2L;
            }
            else
            {
                block.CodecType = BlockCodecType.DenseBitmap;
                block.DenseWords = dense;
                payloadBytes += dense.Length * 8L;
            }

            _blocks[b] = block;
        }

        PayloadBytes = payloadBytes;
        TotalAllocatedBytes = payloadBytes + MetadataBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        long zeroIdx = id - 1;
        int blockIdx = (int)(zeroIdx / BlockSize);
        int offset = (int)(zeroIdx % BlockSize);
        return _blocks[blockIdx].Query(offset);
    }
}
