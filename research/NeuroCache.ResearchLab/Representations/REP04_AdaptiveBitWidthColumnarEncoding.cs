using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.Representations;

public sealed class AbcBlock
{
    public int BaseValue;
    public byte BitWidth;
    public ulong[] PackedWords;

    public AbcBlock(int baseValue, byte bitWidth, int recordCount)
    {
        BaseValue = baseValue;
        BitWidth = bitWidth;
        int totalBits = recordCount * bitWidth;
        int wordCount = (totalBits + 63) / 64;
        PackedWords = new ulong[wordCount];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadValue(int offsetInBlock)
    {
        if (BitWidth == 0) return BaseValue;

        int bitOffset = offsetInBlock * BitWidth;
        int wordIdx = bitOffset >> 6;
        int bitIdx = bitOffset & 63;

        ulong word = PackedWords[wordIdx];
        ulong mask = (1UL << BitWidth) - 1UL;
        ulong val = (word >> bitIdx) & mask;

        if (bitIdx + BitWidth > 64 && wordIdx + 1 < PackedWords.Length)
        {
            int remainingBits = (bitIdx + BitWidth) - 64;
            ulong nextWord = PackedWords[wordIdx + 1];
            ulong nextMask = (1UL << remainingBits) - 1UL;
            val |= (nextWord & nextMask) << (BitWidth - remainingBits);
        }

        return BaseValue + (int)val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteValue(int offsetInBlock, int value)
    {
        if (BitWidth == 0) return;
        ulong delta = (ulong)(value - BaseValue);
        ulong mask = (1UL << BitWidth) - 1UL;
        delta &= mask;

        int bitOffset = offsetInBlock * BitWidth;
        int wordIdx = bitOffset >> 6;
        int bitIdx = bitOffset & 63;

        PackedWords[wordIdx] &= ~(mask << bitIdx);
        PackedWords[wordIdx] |= (delta << bitIdx);

        if (bitIdx + BitWidth > 64 && wordIdx + 1 < PackedWords.Length)
        {
            int remainingBits = (bitIdx + BitWidth) - 64;
            ulong nextMask = (1UL << remainingBits) - 1UL;
            PackedWords[wordIdx + 1] &= ~nextMask;
            PackedWords[wordIdx + 1] |= (delta >> (BitWidth - remainingBits));
        }
    }
}

/// <summary>
/// REP-04: Adaptive Bit-Width Columnar Encoding (ABC)
/// Partitions numeric columns into 256-record blocks. Encodes offsets from block base using minimal bit width.
/// </summary>
public sealed class REP04_AdaptiveBitWidthColumnarEncoding : IExperimentCandidate, IExactRecordLookup<long, int>, IMemoryAccountable
{
    public string ExperimentId => "REP-ABC-004";
    public string CandidateName => "Adaptive Bit-Width Columnar Encoding (ABC)";
    public string Family => "Representations";
    public string Description => "Partitions numeric values into 256-value blocks, storing base value + minimal bit-width offsets.";

    public const int BlockSize = 256;
    private readonly AbcBlock[] _blocks;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes { get; private set; }
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => _blocks.Length * 16;

    public REP04_AdaptiveBitWidthColumnarEncoding(long maxId, Func<long, int> valueSupplier)
    {
        _maxId = maxId;
        int blockCount = (int)((maxId + BlockSize - 1) / BlockSize);
        _blocks = new AbcBlock[blockCount];
        long totalBytes = blockCount * 24L;

        for (int b = 0; b < blockCount; b++)
        {
            long startId = (long)b * BlockSize + 1;
            int countInBlock = (int)Math.Min(BlockSize, maxId - startId + 1);

            int min = int.MaxValue;
            int max = int.MinValue;
            var values = new int[countInBlock];

            for (int i = 0; i < countInBlock; i++)
            {
                int val = valueSupplier(startId + i);
                values[i] = val;
                if (val < min) min = val;
                if (val > max) max = val;
            }

            int maxDelta = Math.Max(0, max - min);
            byte bitWidth = maxDelta == 0 ? (byte)0 : (byte)(32 - BitOperations.LeadingZeroCount((uint)maxDelta));

            var block = new AbcBlock(min, bitWidth, countInBlock);
            for (int i = 0; i < countInBlock; i++)
            {
                block.WriteValue(i, values[i]);
            }
            _blocks[b] = block;
            totalBytes += block.PackedWords.Length * 8L;
        }

        TotalAllocatedBytes = totalBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Query(long id)
    {
        long zeroIdx = id - 1;
        int blockIdx = (int)(zeroIdx / BlockSize);
        int offset = (int)(zeroIdx % BlockSize);
        return _blocks[blockIdx].ReadValue(offset);
    }
}
