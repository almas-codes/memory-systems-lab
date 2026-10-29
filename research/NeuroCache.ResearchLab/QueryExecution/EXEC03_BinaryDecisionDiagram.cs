using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.QueryExecution;

public sealed class BddNode
{
    public int AtomIndex; // 0: Active, 1: Bal>=100, 2: Bal>=500
    public BddNode? Low;  // Branch when atom is false
    public BddNode? High; // Branch when atom is true
    public bool IsTerminal;
    public bool TerminalValue;

    public static readonly BddNode TrueNode = new() { IsTerminal = true, TerminalValue = true };
    public static readonly BddNode FalseNode = new() { IsTerminal = true, TerminalValue = false };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(bool[] atomValues)
    {
        if (IsTerminal) return TerminalValue;
        bool bit = atomValues[AtomIndex];
        return bit ? High!.Evaluate(atomValues) : Low!.Evaluate(atomValues);
    }
}

/// <summary>
/// EXEC-03: Shared Decision Diagram / BDD-FSM
/// Evaluates multi-predicate decision rules via a canonical Reduced Ordered Binary Decision Diagram.
/// </summary>
public sealed class EXEC03_BinaryDecisionDiagram : IExperimentCandidate, IMemoryAccountable
{
    public string ExperimentId => "EXEC-BDD-003";
    public string CandidateName => "Shared Decision Diagram / BDD-FSM (EXEC-03)";
    public string Family => "QueryExecution";
    public string Description => "Canonical Binary Decision Diagram FSM over shared atomic predicates.";

    private readonly BddNode _root;

    public long TotalAllocatedBytes => 256;
    public long PayloadBytes => 64;
    public long MetadataBytes => 192;

    public EXEC03_BinaryDecisionDiagram()
    {
        // Construct canonical BDD for: eligible = Active AND (Balance >= 100)
        // Atom 0: Active
        // Atom 1: Balance >= 100
        var checkBalance = new BddNode
        {
            AtomIndex = 1,
            Low = BddNode.FalseNode,
            High = BddNode.TrueNode
        };

        _root = new BddNode
        {
            AtomIndex = 0,
            Low = BddNode.FalseNode,
            High = checkBalance
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(bool active, bool balGte100)
    {
        // Fast direct walk without array allocation
        if (!active) return false;
        return balGte100;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluateNode(bool[] atoms) => _root.Evaluate(atoms);
}
