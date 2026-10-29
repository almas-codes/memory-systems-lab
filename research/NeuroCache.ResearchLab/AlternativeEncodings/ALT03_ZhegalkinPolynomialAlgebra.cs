using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.AlternativeEncodings;

/// <summary>
/// ALT-03: Zhegalkin / Ring-Sum Algebraic Representation
/// Represents boolean query logic through an algebraic polynomial over GF(2) using XOR (ring addition) and AND (ring multiplication).
/// </summary>
public sealed class ALT03_ZhegalkinPolynomialAlgebra : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "ALT-ZHEGALKIN-003";
    public string CandidateName => "Zhegalkin / Ring-Sum Algebraic Representation (ALT-03)";
    public string Family => "AlternativeEncodings";
    public string Description => "Represents boolean predicates algebraically over GF(2) via Zhegalkin polynomial normal forms.";

    public long TotalAllocatedBytes => 128;
    public long PayloadBytes => 32;
    public long MetadataBytes => 96;

    /// <summary>
    /// Evaluates: Eligible = Active AND Gte100 over GF(2).
    /// In GF(2) ring-sum form, AND is multiplication (*), XOR is addition (+).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluateEligibility(bool active, bool gte100)
    {
        // P(x1, x2) = x1 * x2 (mod 2)
        int x1 = active ? 1 : 0;
        int x2 = gte100 ? 1 : 0;
        return (x1 & x2) == 1;
    }

    /// <summary>
    /// Evaluates composite: Active AND NOT Gte100
    /// Ring-sum form: x1 * (1 ^ x2) = x1 ^ (x1 * x2)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluateActiveUnder100(bool active, bool gte100)
    {
        int x1 = active ? 1 : 0;
        int x2 = gte100 ? 1 : 0;
        return (x1 ^ (x1 & x2)) == 1;
    }
}
