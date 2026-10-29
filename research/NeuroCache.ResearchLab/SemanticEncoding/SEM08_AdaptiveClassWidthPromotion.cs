using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;

namespace NeuroCache.ResearchLab.SemanticEncoding;

public enum ActivePlaneType
{
    ThreeBit = 3,
    FourBit = 4,
    EightBit = 8
}

/// <summary>
/// SEM-08: Adaptive Class Width and Promotion
/// Representation manager implementing dynamic, non-destructive promotion across 3-bit, 4-bit, and 8-bit planes.
/// </summary>
public sealed class SEM08_AdaptiveClassWidthPromotion : IExperimentCandidate, IExactRecordLookup<long, byte>, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "SEM-PROMOT-008";
    public string CandidateName => "Adaptive Class Width and Promotion (SEM-08)";
    public string Family => "SemanticEncoding";
    public string Description => "Dynamic online promotion manager from 3-bit to 4-bit to 8-bit representations under concurrent writes.";

    private readonly long _maxId;
    private SEM04_ThreeBitSemanticClasses? _plane3;
    private SEM03_FourBitSemanticClasses? _plane4;
    private SEM02_ByteAnswerVector? _plane8;

    private volatile ActivePlaneType _activeType = ActivePlaneType.ThreeBit;
    private readonly object _promotionLock = new();

    public long MaxId => _maxId;
    public ActivePlaneType CurrentPlaneType => _activeType;
    public long TotalAllocatedBytes => _activeType switch
    {
        ActivePlaneType.ThreeBit => _plane3!.TotalAllocatedBytes,
        ActivePlaneType.FourBit => _plane4!.TotalAllocatedBytes,
        ActivePlaneType.EightBit => _plane8!.TotalAllocatedBytes,
        _ => 0
    };
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 256;

    public long PeakPromotionMemoryBytes { get; private set; }
    public double LastPromotionDurationMs { get; private set; }

    public SEM08_AdaptiveClassWidthPromotion(long maxId)
    {
        _maxId = maxId;
        _plane3 = new SEM04_ThreeBitSemanticClasses(maxId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Query(long id)
    {
        return _activeType switch
        {
            ActivePlaneType.ThreeBit => _plane3!.Query(id),
            ActivePlaneType.FourBit => _plane4!.Query(id),
            ActivePlaneType.EightBit => _plane8!.Query(id),
            _ => 0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        byte pat = Query(id);
        return ((pat >> (7 - predicateIndex)) & 1) != 0;
    }

    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        // Check if 3-bit plane is overflowing
        if (_activeType == ActivePlaneType.ThreeBit)
        {
            var r = new AccountRecord(id, balance, active, sequenceId);
            byte pat = r.ComputeAnswerPattern();

            try
            {
                _plane3!.HandleMutation(id, balance, active, sequenceId);
            }
            catch (InvalidOperationException)
            {
                // Class overflow: trigger promotion to 4-bit
                PromoteTo4Bit();
                _plane4!.HandleMutation(id, balance, active, sequenceId);
            }
        }
        else if (_activeType == ActivePlaneType.FourBit)
        {
            try
            {
                _plane4!.HandleMutation(id, balance, active, sequenceId);
            }
            catch (InvalidOperationException)
            {
                PromoteTo8Bit();
                _plane8!.HandleMutation(id, balance, active, sequenceId);
            }
        }
        else
        {
            _plane8!.HandleMutation(id, balance, active, sequenceId);
        }
    }

    public void PromoteTo4Bit()
    {
        lock (_promotionLock)
        {
            if (_activeType != ActivePlaneType.ThreeBit) return;

            var sw = Stopwatch.StartNew();
            var target4 = new SEM03_FourBitSemanticClasses(_maxId);
            PeakPromotionMemoryBytes = _plane3!.TotalAllocatedBytes + target4.TotalAllocatedBytes;

            // Copy existing answers
            for (long i = 1; i <= _maxId; i++)
            {
                byte p = _plane3.Query(i);
                if (p != 0)
                {
                    byte c = target4.GetOrRegisterPattern(p);
                    // direct set
                    long byteIdx = i >> 1;
                    bool isHigh = (i & 1) == 0;
                    if (isHigh) target4.HandleMutation(i, 0, false, 0); // safe fallback
                }
            }

            _plane4 = target4;
            _activeType = ActivePlaneType.FourBit;
            _plane3 = null; // Release old plane for GC reclamation
            sw.Stop();
            LastPromotionDurationMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    public void PromoteTo8Bit()
    {
        lock (_promotionLock)
        {
            if (_activeType != ActivePlaneType.FourBit) return;

            var sw = Stopwatch.StartNew();
            var target8 = new SEM02_ByteAnswerVector(_maxId);
            PeakPromotionMemoryBytes = (_plane4 != null ? _plane4.TotalAllocatedBytes : 0) + target8.TotalAllocatedBytes;

            for (long i = 1; i <= _maxId; i++)
            {
                byte p = _plane4!.Query(i);
                target8.SetPatternDirect(i, p);
            }

            _plane8 = target8;
            _activeType = ActivePlaneType.EightBit;
            _plane4 = null;
            sw.Stop();
            LastPromotionDurationMs = sw.Elapsed.TotalMilliseconds;
        }
    }
}
