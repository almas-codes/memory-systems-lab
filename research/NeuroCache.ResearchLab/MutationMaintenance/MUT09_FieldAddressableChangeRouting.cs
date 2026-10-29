using System;
using System.Threading;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.ThresholdEncoding;

namespace NeuroCache.ResearchLab.MutationMaintenance;

/// <summary>
/// MUT-09: Field-Addressable Change Routing
/// Routes mutations specifically to only the dependent answer planes and threshold encodings affected by changed fields.
/// </summary>
public sealed class MUT09_FieldAddressableChangeRouting : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "MUT-ROUTER-009";
    public string CandidateName => "Field-Addressable Change Routing (MUT-09)";
    public string Family => "MutationMaintenance";
    public string Description => "Routes changes selectively to only the planes and directories dependent on modified fields.";

    private long _routedToBalancePlanes;
    private long _routedToStatusPlanes;
    private long _routedToJointPlanes;
    private long _totalIncoming;

    public long TotalIncoming => Volatile.Read(ref _totalIncoming);
    public long RoutedToBalancePlanes => Volatile.Read(ref _routedToBalancePlanes);
    public long RoutedToStatusPlanes => Volatile.Read(ref _routedToStatusPlanes);
    public long RoutedToJointPlanes => Volatile.Read(ref _routedToJointPlanes);

    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 32;
    public long MetadataBytes => 96;

    public void RouteMutation(
        long id,
        AccountFieldMask changedFields,
        int newBalance,
        bool newActive,
        THR01_ThresholdDecisionDirectory? thresholdDir,
        MUT01_SparseMutationOverlay? overlay)
    {
        Interlocked.Increment(ref _totalIncoming);

        bool balanceChanged = (changedFields & AccountFieldMask.Balance) != 0;
        bool statusChanged = (changedFields & AccountFieldMask.Active) != 0;

        if (balanceChanged)
        {
            Interlocked.Increment(ref _routedToBalancePlanes);
            thresholdDir?.SetBalance(id, newBalance);
        }

        if (statusChanged)
        {
            Interlocked.Increment(ref _routedToStatusPlanes);
        }

        if (balanceChanged || statusChanged)
        {
            Interlocked.Increment(ref _routedToJointPlanes);
            overlay?.HandleMutation(id, newBalance, newActive, 0);
        }
    }
}
