using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NeuroCache.EapResearch;

/// <summary>
/// Thread-safe tracker that guarantees the global last-applied watermark advances
/// strictly contiguously without skipping in-flight or delayed publications.
/// Prevents the cross-account watermark gap anomaly where an earlier transaction
/// that committed to PostgreSQL is skipped by recovery checkpoints.
/// </summary>
public sealed class ContiguousWatermarkTracker
{
    private long _contiguousWatermark;
    private long _highestPublishedSeq;
    private readonly object _lock = new();
    private readonly HashSet<long> _inFlight = new();

    public long Watermark
    {
        get
        {
            lock (_lock)
            {
                return _contiguousWatermark;
            }
        }
    }

    public long HighestPublishedSeq
    {
        get
        {
            lock (_lock)
            {
                return _highestPublishedSeq;
            }
        }
    }

    public int InFlightCount
    {
        get
        {
            lock (_lock)
            {
                return _inFlight.Count;
            }
        }
    }

    public ContiguousWatermarkTracker(long initialWatermark = 0)
    {
        _contiguousWatermark = initialWatermark;
        _highestPublishedSeq = initialWatermark;
    }

    public void Reset(long initialWatermark)
    {
        lock (_lock)
        {
            _contiguousWatermark = initialWatermark;
            _highestPublishedSeq = initialWatermark;
            _inFlight.Clear();
        }
    }

    /// <summary>
    /// Registers a committed mutation sequence as in-flight (committed to PostgreSQL, awaiting memory publication).
    /// </summary>
    public void RegisterInFlight(long seqId)
    {
        if (seqId <= 0) return;
        lock (_lock)
        {
            _inFlight.Add(seqId);
        }
    }

    /// <summary>
    /// Cancels an in-flight sequence if a transaction rolled back or aborted.
    /// </summary>
    public void CancelInFlight(long seqId)
    {
        if (seqId <= 0) return;
        lock (_lock)
        {
            _inFlight.Remove(seqId);
            AdvanceWatermarkUnderLock();
        }
    }

    /// <summary>
    /// Acknowledges completion of in-memory publication for the given sequence.
    /// Removes seqId from in-flight and advances the contiguous watermark up to the earliest
    /// still-in-flight sequence minus 1, or to the highest published sequence if none are in-flight.
    /// </summary>
    public long AcknowledgePublication(long seqId)
    {
        if (seqId <= 0) return Watermark;

        lock (_lock)
        {
            _inFlight.Remove(seqId);
            if (seqId > _highestPublishedSeq)
            {
                _highestPublishedSeq = seqId;
            }

            AdvanceWatermarkUnderLock();
            return _contiguousWatermark;
        }
    }

    private void AdvanceWatermarkUnderLock()
    {
        if (_inFlight.Count == 0)
        {
            // All committed transactions have completed publication: watermark advances to highest published sequence
            if (_highestPublishedSeq > _contiguousWatermark)
            {
                _contiguousWatermark = _highestPublishedSeq;
            }
        }
        else
        {
            // At least one committed transaction is still in-flight:
            // The watermark CANNOT advance past the minimum in-flight sequence!
            long minInFlight = _inFlight.Min();
            long safeWatermark = minInFlight - 1;
            if (safeWatermark > _contiguousWatermark)
            {
                _contiguousWatermark = safeWatermark;
            }
        }
    }
}
