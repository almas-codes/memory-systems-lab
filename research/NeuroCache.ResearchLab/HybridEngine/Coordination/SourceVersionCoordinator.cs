using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.HybridEngine.Contracts;

namespace NeuroCache.ResearchLab.HybridEngine.Coordination;

public sealed class SourceVersionCoordinator : ISourceVersionCoordinator
{
    private long _currentSourceVersion;
    private long _publishedWatermark;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _versionWaiters = new();
    private readonly IRepresentationPublisher _publisher;
    private readonly object _commitLock = new();

    public long CurrentSourceVersion => Volatile.Read(ref _currentSourceVersion);
    public long PublishedWatermark => Volatile.Read(ref _publishedWatermark);

    public SourceVersionCoordinator(IRepresentationPublisher publisher, long initialVersion = 0)
    {
        _publisher = publisher;
        _currentSourceVersion = initialVersion;
        _publishedWatermark = initialVersion;
    }

    public long AllocateCommitSequence()
    {
        return Interlocked.Increment(ref _currentSourceVersion);
    }

    public void CommitTransaction(long commitSeq, IEnumerable<(long accountId, int balance, bool active)> mutations)
    {
        lock (_commitLock)
        {
            var mutationList = mutations as IList<(long accountId, int balance, bool active)> ?? new List<(long accountId, int balance, bool active)>(mutations);

            // Pass through publisher
            _publisher.PublishBatch(commitSeq, mutationList);

            // Advance watermark monotonically
            long curWatermark = Volatile.Read(ref _publishedWatermark);
            if (commitSeq > curWatermark)
            {
                Volatile.Write(ref _publishedWatermark, commitSeq);
            }

            // Wake up any waiters for this or earlier versions
            foreach (var kvp in _versionWaiters)
            {
                if (kvp.Key <= commitSeq && _versionWaiters.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetResult(true);
                }
            }
        }
    }

    public async Task<bool> WaitForVersionAsync(long requiredVersion, TimeSpan timeout, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _publishedWatermark) >= requiredVersion)
        {
            return true;
        }

        var tcs = _versionWaiters.GetOrAdd(requiredVersion, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        // Recheck after registering waiter
        if (Volatile.Read(ref _publishedWatermark) >= requiredVersion)
        {
            _versionWaiters.TryRemove(requiredVersion, out _);
            return true;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using (cts.Token.Register(() => tcs.TrySetResult(false)))
        {
            try
            {
                return await tcs.Task;
            }
            finally
            {
                _versionWaiters.TryRemove(requiredVersion, out _);
            }
        }
    }

    public long GetPublishedWatermark() => Volatile.Read(ref _publishedWatermark);
}
