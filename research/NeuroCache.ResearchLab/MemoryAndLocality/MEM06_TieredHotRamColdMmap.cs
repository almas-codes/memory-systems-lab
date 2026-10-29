using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MemoryAndLocality;

/// <summary>
/// MEM-06: Tiered Hot-RAM / Cold-Mmap (THCM)
/// Partitions IDs into a high-speed resident RAM array for hot IDs and a memory-mapped file for cold IDs.
/// </summary>
public sealed class MEM06_TieredHotRamColdMmap : IExperimentCandidate, IExactRecordLookup<long, byte>, IDisposable, IMemoryAccountable
{
    public string ExperimentId => "MEM-THCM-006";
    public string CandidateName => "Tiered Hot-RAM / Cold-Mmap (THCM / MEM-06)";
    public string Family => "MemoryAndLocality";
    public string Description => "Tiers storage: hot ID range in pure RAM; cold ID range in memory-mapped file.";

    private readonly byte[] _hotRam;
    private readonly long _hotThresholdId;
    private readonly long _maxId;
    private readonly MemoryMappedFile _mmapFile;
    private readonly MemoryMappedViewAccessor _accessor;

    public long MaxId => _maxId;
    public long HotThresholdId => _hotThresholdId;

    public long TotalAllocatedBytes => _hotRam.LongLength + (_maxId - _hotThresholdId);
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 512;

    public MEM06_TieredHotRamColdMmap(long maxId, long hotThresholdId = 100_000)
    {
        _maxId = maxId;
        _hotThresholdId = Math.Min(maxId, hotThresholdId);
        _hotRam = new byte[_hotThresholdId + 1];

        long coldBytes = Math.Max(1024, maxId - _hotThresholdId + 1);
        string tempPath = Path.Combine(Path.GetTempPath(), $"neurocache_cold_{Guid.NewGuid():N}.dat");
        _mmapFile = MemoryMappedFile.CreateFromFile(
            tempPath,
            FileMode.CreateNew,
            mapName: null,
            capacity: coldBytes,
            MemoryMappedFileAccess.ReadWrite);

        _accessor = _mmapFile.CreateViewAccessor(0, coldBytes, MemoryMappedFileAccess.ReadWrite);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        if (id <= _hotThresholdId)
        {
            return _hotRam[id];
        }
        long coldOffset = id - _hotThresholdId;
        return _accessor.ReadByte(coldOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAnswer(long id, byte pattern)
    {
        if (id <= _hotThresholdId)
        {
            _hotRam[id] = pattern;
        }
        else
        {
            long coldOffset = id - _hotThresholdId;
            _accessor.Write(coldOffset, pattern);
        }
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmapFile.Dispose();
    }
}
