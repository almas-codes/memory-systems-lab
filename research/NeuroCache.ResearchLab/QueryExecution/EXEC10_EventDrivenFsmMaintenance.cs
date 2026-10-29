using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.QueryExecution;

public enum FsmEligibilityState : byte
{
    IneligibleInactive = 0,
    IneligibleLowBalance = 1,
    Eligible = 2
}

public enum AccountEventType : byte
{
    Deactivated,
    Reactivated,
    BalanceBelow100,
    BalanceAboveOrEqual100
}

/// <summary>
/// EXEC-10: Event-Driven FSM Answer Maintenance
/// Represents answer as an explicit Finite State Machine driven by domain lifecycle events.
/// </summary>
public sealed class EXEC10_EventDrivenFsmMaintenance : IExperimentCandidate, IExactRecordLookup<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "EXEC-FSM-010";
    public string CandidateName => "Event-Driven FSM Answer Maintenance (EXEC-10)";
    public string Family => "QueryExecution";
    public string Description => "State-machine driven answers updated directly via typed lifecycle transition events.";

    private readonly FsmEligibilityState[] _states;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _states.LongLength;
    public long PayloadBytes => _states.LongLength;
    public long MetadataBytes => 64;

    public EXEC10_EventDrivenFsmMaintenance(long maxId)
    {
        _maxId = maxId;
        _states = new FsmEligibilityState[maxId + 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Query(long id) => _states[id] == FsmEligibilityState.Eligible;

    public void ApplyEvent(long id, AccountEventType evt)
    {
        var cur = _states[id];
        _states[id] = (cur, evt) switch
        {
            // Deactivation moves to IneligibleInactive regardless of balance
            (_, AccountEventType.Deactivated) => FsmEligibilityState.IneligibleInactive,

            // Reactivation moves to Eligible or LowBalance
            (FsmEligibilityState.IneligibleInactive, AccountEventType.Reactivated) => FsmEligibilityState.IneligibleLowBalance,

            // Balance transitions
            (FsmEligibilityState.IneligibleLowBalance, AccountEventType.BalanceAboveOrEqual100) => FsmEligibilityState.Eligible,
            (FsmEligibilityState.Eligible, AccountEventType.BalanceBelow100) => FsmEligibilityState.IneligibleLowBalance,

            // No-op transitions
            _ => cur
        };
    }

    public void HydrateInitialState(long id, bool active, int balance)
    {
        if (!active) _states[id] = FsmEligibilityState.IneligibleInactive;
        else if (balance >= 100) _states[id] = FsmEligibilityState.Eligible;
        else _states[id] = FsmEligibilityState.IneligibleLowBalance;
    }
}
