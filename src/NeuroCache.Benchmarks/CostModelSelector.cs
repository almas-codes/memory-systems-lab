using System;
using System.Diagnostics;
using NeuroCache.Core;

namespace NeuroCache.Benchmarks;

public enum RoutingAction
{
    InlineReuse,
    Recompute,
    ReadExtent,
    ReadCapsule,
    ProjectOnDemand,
    MaterializeCapsule,
    AuthoritativeFallback,
    DoNothing
}

public sealed class CostProfile
{
    public double InlineLookupNs { get; set; } = 35.0;            // ~35 ns
    public double WitnessValidationNs { get; set; } = 15.0;       // ~15 ns
    public double PureRecomputeNs { get; set; } = 12.0;           // ~12 ns
    public double MemoryMappedReadNs { get; set; } = 350.0;       // ~350 ns (page cache)
    public double SsdReadLatencyUs { get; set; } = 85.0;          // ~85 us NVMe read
    public double CapsuleReadLatencyUs { get; set; } = 65.0;      // ~65 us single-sector NVMe read
    public double PayloadDecodeNsPerByte { get; set; } = 0.5;     // SIMD decode
    public double CapsuleWriteLatencyUs { get; set; } = 120.0;    // ~120 us write
    public long MetadataBytesPerClaim { get; set; } = 48;
}

public sealed class CostModelSelector
{
    private readonly CostProfile _profile;

    public CostModelSelector(CostProfile? profile = null)
    {
        _profile = profile ?? new CostProfile();
    }

    public RoutingAction SelectOptimalRoute(
        OperationType operation,
        bool hasInlineAnswer,
        bool hasRecomputeSeed,
        bool hasCapsule,
        bool isWitnessValid,
        long currentRamUsageBytes,
        long ramBudgetBytes)
    {
        // Hard constraint check: RAM budget
        bool ramConstrained = currentRamUsageBytes >= ramBudgetBytes;

        if (hasInlineAnswer && isWitnessValid)
        {
            return RoutingAction.InlineReuse;
        }

        if (operation is OperationType.ComputeDerived or OperationType.EvaluatePredicate)
        {
            if (hasRecomputeSeed && isWitnessValid)
            {
                // Pure recomputation: takes ~12ns + 15ns = ~27ns.
                // An SSD read takes ~85,000ns.
                // Recomputation is ~3,000x faster than NVMe SSD read!
                return RoutingAction.Recompute;
            }
        }

        if (operation is OperationType.FieldProjection)
        {
            if (hasRecomputeSeed && isWitnessValid)
            {
                return RoutingAction.InlineReuse;
            }

            if (hasCapsule)
            {
                return RoutingAction.ReadCapsule;
            }

            return RoutingAction.ProjectOnDemand;
        }

        if (hasCapsule && isWitnessValid)
        {
            return RoutingAction.ReadCapsule;
        }

        return RoutingAction.ReadExtent;
    }
}
