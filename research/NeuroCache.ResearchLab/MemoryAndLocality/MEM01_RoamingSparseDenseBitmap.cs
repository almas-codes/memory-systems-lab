using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

public enum RsdContainerType : byte
{
    SparseArray = 0,
    DenseBitmap = 1
}

public sealed class RsdChunk
{
    public RsdContainerType Type;
    public ulong[]? DenseWords;
    public ushort[]? SparseArray;
    public int Cardinality;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(int offsetInChunk) => Type switch
    {
        RsdContainerType.DenseBitmap => (DenseWords![offsetInChunk >> 6] & (1UL << (offsetInChunk & 63))) != 0,
        RsdContainerType.SparseArray => Array.BinarySearch(SparseArray!, (ushort)offsetInChunk) >= 0,
        _ => false
    };
}

/// <summary>
/// MEM-01: Roaming Sparse/Dense Bitmap (RSD)
/// Roaring-style chunked bitmap switching between sparse integer arrays and dense 64-bit words per 4096-bit chunk.
/// </summary>
public sealed class MEM01_RoamingSparseDenseBitmap : IExperimentCandidate, IExactRecordLookup<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "MEM-ROARING-001";
    public string CandidateName => "Roaming Sparse/Dense Bitmap (RSD / MEM-01)";
    public string Family => "MemoryAndLocality";
    public string Description => "Chunked representation dynamically choosing sparse arrays or dense bitmaps per 4096-record chunk.";

    public const int ChunkSize = 4096;
    private readonly RsdChunk[] _chunks;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes { get; private set; }
    public long PayloadBytes { get; private set; }
    public long MetadataBytes => _chunks.Length * 24L;

    public MEM01_RoamingSparseDenseBitmap(long maxId, Func<long, bool> predicateSupplier)
    {
        _maxId = maxId;
        int chunkCount = (int)((maxId + ChunkSize - 1) / ChunkSize);
        _chunks = new RsdChunk[chunkCount];
        long payload = 0;

        for (int c = 0; c < chunkCount; c++)
        {
            long startId = (long)c * ChunkSize + 1;
            int countInChunk = (int)Math.Min(ChunkSize, maxId - startId + 1);

            var sparseList = new List<ushort>();
            var dense = new ulong[(countInChunk + 63) / 64];

            for (int i = 0; i < countInChunk; i++)
            {
                if (predicateSupplier(startId + i))
                {
                    sparseList.Add((ushort)i);
                    dense[i >> 6] |= (1UL << (i & 63));
                }
            }

            var chunk = new RsdChunk { Cardinality = sparseList.Count };
            // Roaring threshold: if cardinality <= 256, use 16-bit array (512 bytes max)
            // If > 256, use dense bitmap (512 bytes fixed)
            if (sparseList.Count <= 256)
            {
                chunk.Type = RsdContainerType.SparseArray;
                chunk.SparseArray = sparseList.ToArray();
                payload += chunk.SparseArray.Length * 2L;
            }
            else
            {
                chunk.Type = RsdContainerType.DenseBitmap;
                chunk.DenseWords = dense;
                payload += dense.Length * 8L;
            }

            _chunks[c] = chunk;
        }

        PayloadBytes = payload;
        TotalAllocatedBytes = payload + MetadataBytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        long zero = id - 1;
        int chunkIdx = (int)(zero / ChunkSize);
        int offset = (int)(zero % ChunkSize);
        return _chunks[chunkIdx].Contains(offset);
    }
}
