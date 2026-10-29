using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

/// <summary>
/// MEM-05: Succinct Rank/Select Bit Vector
/// Implements O(1) Rank (number of 1s up to position i) and Select (position of k-th 1) with compact directory indexing.
/// </summary>
public sealed class MEM05_SuccinctRankSelectVector : IExperimentCandidate, IExactRecordLookup<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "MEM-RANKSEL-005";
    public string CandidateName => "Succinct Rank/Select Bit Vector (MEM-05)";
    public string Family => "MemoryAndLocality";
    public string Description => "Succinct bitvector with auxiliary superblocks supporting O(1) Rank and binary-search Select.";

    private readonly ulong[] _bits;
    private readonly int[] _superBlocks; // 512 bits (8 words) per superblock
    private const int SuperBlockBits = 512;
    private const int WordsPerSuperBlock = 8;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => (_bits.Length * 8L) + (_superBlocks.Length * 4L);
    public long PayloadBytes => _bits.Length * 8L;
    public long MetadataBytes => _superBlocks.Length * 4L;

    public MEM05_SuccinctRankSelectVector(long maxId, Func<long, bool> bitSupplier)
    {
        _maxId = maxId;
        int wordCount = (int)((maxId + 63) / 64);
        _bits = new ulong[wordCount];

        for (long id = 1; id <= maxId; id++)
        {
            if (bitSupplier(id))
            {
                long zero = id - 1;
                _bits[zero >> 6] |= (1UL << (int)(zero & 63));
            }
        }

        // Build SuperBlock Directory for Rank
        int superBlockCount = (int)((maxId + SuperBlockBits - 1) / SuperBlockBits);
        _superBlocks = new int[superBlockCount + 1];

        int cumulative = 0;
        for (int sb = 0; sb < superBlockCount; sb++)
        {
            _superBlocks[sb] = cumulative;
            int startWord = sb * WordsPerSuperBlock;
            int endWord = Math.Min(wordCount, startWord + WordsPerSuperBlock);
            for (int w = startWord; w < endWord; w++)
            {
                cumulative += BitOperations.PopCount(_bits[w]);
            }
        }
        _superBlocks[superBlockCount] = cumulative;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        long zero = id - 1;
        return (_bits[zero >> 6] & (1UL << (int)(zero & 63))) != 0;
    }

    /// <summary>
    /// Computes number of 1-bits in range [1 .. id] in O(1) time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Rank(long id)
    {
        if (id <= 0) return 0;
        long zero = Math.Min(id - 1, _maxId - 1);
        int sbIdx = (int)(zero / SuperBlockBits);
        int rank = _superBlocks[sbIdx];

        int wordIdx = (int)(zero >> 6);
        int startWord = sbIdx * WordsPerSuperBlock;

        for (int w = startWord; w < wordIdx; w++)
        {
            rank += BitOperations.PopCount(_bits[w]);
        }

        int bitOffset = (int)(zero & 63);
        ulong mask = bitOffset == 63 ? ~0UL : (1UL << (bitOffset + 1)) - 1UL;
        rank += BitOperations.PopCount(_bits[wordIdx] & mask);

        return rank;
    }

    /// <summary>
    /// Finds the 1-based account ID where the k-th 1-bit occurs.
    /// </summary>
    public long Select(int k)
    {
        if (k <= 0) return -1;
        // Binary search over superblocks
        int low = 0;
        int high = _superBlocks.Length - 1;
        int sb = 0;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_superBlocks[mid] < k)
            {
                sb = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        int currentRank = _superBlocks[sb];
        int startWord = sb * WordsPerSuperBlock;
        int endWord = Math.Min(_bits.Length, startWord + WordsPerSuperBlock);

        for (int w = startWord; w < endWord; w++)
        {
            int pop = BitOperations.PopCount(_bits[w]);
            if (currentRank + pop >= k)
            {
                // Found word: bitwise search
                for (int b = 0; b < 64; b++)
                {
                    if ((_bits[w] & (1UL << b)) != 0)
                    {
                        currentRank++;
                        if (currentRank == k)
                        {
                            return (long)w * 64 + b + 1;
                        }
                    }
                }
            }
            currentRank += pop;
        }

        return -1; // Not found
    }
}
