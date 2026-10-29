using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.SemanticEncoding;

public sealed class BlockLocalDictionary
{
    public byte[] LocalClasses = Array.Empty<byte>();
    public byte BitWidth;
    public byte[] PackedData = Array.Empty<byte>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(int offsetInBlock)
    {
        if (BitWidth == 0) return LocalClasses.Length > 0 ? LocalClasses[0] : (byte)0;
        if (BitWidth == 1)
        {
            int byteIdx = offsetInBlock >> 3;
            int bit = offsetInBlock & 7;
            int idx = (PackedData[byteIdx] >> bit) & 1;
            return idx < LocalClasses.Length ? LocalClasses[idx] : (byte)0;
        }
        else if (BitWidth == 2)
        {
            int byteIdx = offsetInBlock >> 2;
            int shift = (offsetInBlock & 3) * 2;
            int idx = (PackedData[byteIdx] >> shift) & 3;
            return idx < LocalClasses.Length ? LocalClasses[idx] : (byte)0;
        }
        else
        {
            int byteIdx = offsetInBlock >> 1;
            int shift = (offsetInBlock & 1) == 0 ? 4 : 0;
            int idx = (PackedData[byteIdx] >> shift) & 0x0F;
            return idx < LocalClasses.Length ? LocalClasses[idx] : (byte)0;
        }
    }
}

/// <summary>
/// SEM-07: Block-Local Semantic Dictionaries
/// Partitions dataset into 512-record blocks, each maintaining a local dictionary tailored to its own classes.
/// </summary>
public sealed class SEM07_BlockLocalSemanticDictionaries : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, IMemoryAccountable
{
    public string ExperimentId => "SEM-BLOCKDICT-007";
    public string CandidateName => "Block-Local Semantic Dictionaries (SEM-07)";
    public string Family => "SemanticEncoding";
    public string Description => "Maintains independent local dictionaries per 512-record block with local bit-width adaptation.";

    public const int BlockSize = 512;
    private readonly BlockLocalDictionary[] _blocks;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes { get; private set; }
    public long PayloadBytes { get; private set; }
    public long MetadataBytes => _blocks.Length * 24L;

    public SEM07_BlockLocalSemanticDictionaries(long maxId, Func<long, byte> patternSupplier)
    {
        _maxId = maxId;
        int blockCount = (int)((maxId + BlockSize - 1) / BlockSize);
        _blocks = new BlockLocalDictionary[blockCount];
        long payload = 0;

        for (int b = 0; b < blockCount; b++)
        {
            long startId = (long)b * BlockSize + 1;
            int countInBlock = (int)Math.Min(BlockSize, maxId - startId + 1);

            var seen = new List<byte>();
            var patterns = new byte[countInBlock];

            for (int i = 0; i < countInBlock; i++)
            {
                byte p = patternSupplier(startId + i);
                patterns[i] = p;
                if (!seen.Contains(p)) seen.Add(p);
            }

            var block = new BlockLocalDictionary();
            block.LocalClasses = seen.ToArray();
            payload += block.LocalClasses.Length;

            if (seen.Count <= 1)
            {
                block.BitWidth = 0;
            }
            else if (seen.Count <= 2)
            {
                block.BitWidth = 1;
                block.PackedData = new byte[(countInBlock + 7) / 8];
                for (int i = 0; i < countInBlock; i++)
                {
                    int idx = seen.IndexOf(patterns[i]);
                    block.PackedData[i >> 3] |= (byte)(idx << (i & 7));
                }
                payload += block.PackedData.Length;
            }
            else if (seen.Count <= 4)
            {
                block.BitWidth = 2;
                block.PackedData = new byte[(countInBlock + 3) / 4];
                for (int i = 0; i < countInBlock; i++)
                {
                    int idx = seen.IndexOf(patterns[i]);
                    block.PackedData[i >> 2] |= (byte)(idx << ((i & 3) * 2));
                }
                payload += block.PackedData.Length;
            }
            else
            {
                block.BitWidth = 4;
                block.PackedData = new byte[(countInBlock + 1) / 2];
                for (int i = 0; i < countInBlock; i++)
                {
                    int idx = seen.IndexOf(patterns[i]);
                    if ((i & 1) == 0) block.PackedData[i >> 1] |= (byte)(idx << 4);
                    else block.PackedData[i >> 1] |= (byte)(idx & 0x0F);
                }
                payload += block.PackedData.Length;
            }

            _blocks[b] = block;
        }

        PayloadBytes = payload;
        TotalAllocatedBytes = payload + MetadataBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        long zero = id - 1;
        int blockIdx = (int)(zero / BlockSize);
        int offset = (int)(zero % BlockSize);
        return _blocks[blockIdx].Query(offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        byte pat = Query(id);
        return ((pat >> (7 - predicateIndex)) & 1) != 0;
    }
}
