using System;
using System.Runtime.CompilerServices;
using System.Threading;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.MutationMaintenance;

public sealed class MvccGeneration
{
    public long GenerationId { get; }
    public EapBooleanBitmap Bitmap { get; }
    private int _pinnedReaders;

    public MvccGeneration(long generationId, long maxId, EapBooleanBitmap? seed = null)
    {
        GenerationId = generationId;
        Bitmap = new EapBooleanBitmap(maxId);
        if (seed != null)
        {
            // Clone seed bitmap state
            for (long i = 1; i <= Math.Min(maxId, seed.MaxId); i++)
            {
                if (seed.IsEligible(i)) Bitmap.SetEligible(i, true);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Pin() => Interlocked.Increment(ref _pinnedReaders);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unpin() => Interlocked.Decrement(ref _pinnedReaders);

    public int PinnedReaders => Volatile.Read(ref _pinnedReaders);
}

/// <summary>
/// MUT-02: Multi-Generation MVCC Planes (MGP)
/// Readers pin an immutable generation snapshot while writers construct replacement generations.
/// </summary>
public sealed class MUT02_MultiGenerationMvccPlanes : IExperimentCandidate, IExactRecordLookup<long, bool>, IGenerationPublisher, IMemoryAccountable
{
    public string ExperimentId => "MUT-MGP-002";
    public string CandidateName => "Multi-Generation MVCC Planes (MGP)";
    public string Family => "MutationMaintenance";
    public string Description => "Immutable snapshot generations with reader pinning and atomic root swaps.";

    private volatile MvccGeneration _activeGeneration;
    private long _generationCounter = 1;
    private readonly long _maxId;
    private readonly object _writerLock = new();

    public long MaxId => _maxId;
    public long ActiveGeneration => _activeGeneration.GenerationId;
    public long TotalAllocatedBytes => _activeGeneration.Bitmap.RawMemoryBytes;
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 128;

    public MUT02_MultiGenerationMvccPlanes(long maxId)
    {
        _maxId = maxId;
        _activeGeneration = new MvccGeneration(1, maxId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id)
    {
        var gen = _activeGeneration;
        gen.Pin();
        try
        {
            return gen.Bitmap.IsEligible(id);
        }
        finally
        {
            gen.Unpin();
        }
    }

    public void MutateAndPublish(Action<EapBooleanBitmap> mutator)
    {
        lock (_writerLock)
        {
            long newGenId = Interlocked.Increment(ref _generationCounter);
            var nextGen = new MvccGeneration(newGenId, _maxId, _activeGeneration.Bitmap);
            mutator(nextGen.Bitmap);

            // Atomic generation swap
            _activeGeneration = nextGen;
        }
    }

    public void PublishGeneration()
    {
        MutateAndPublish(_ => { });
    }
}
