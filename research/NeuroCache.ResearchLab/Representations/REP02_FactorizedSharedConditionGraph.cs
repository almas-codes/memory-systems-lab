using System;
using System.Runtime.CompilerServices;
using NeuroCache.EapResearch;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.Representations;

public enum GraphOp
{
    Atom,
    And,
    Or,
    Not
}

public sealed class PredicateNode
{
    public GraphOp Op { get; }
    public int AtomIndex { get; }
    public PredicateNode? Left { get; }
    public PredicateNode? Right { get; }

    public PredicateNode(int atomIndex)
    {
        Op = GraphOp.Atom;
        AtomIndex = atomIndex;
    }

    public PredicateNode(GraphOp op, PredicateNode left, PredicateNode? right = null)
    {
        Op = op;
        Left = left;
        Right = right;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Evaluate(EapBooleanBitmap[] atoms, long id) => Op switch
    {
        GraphOp.Atom => atoms[AtomIndex].IsEligible(id),
        GraphOp.And => Left!.Evaluate(atoms, id) && Right!.Evaluate(atoms, id),
        GraphOp.Or => Left!.Evaluate(atoms, id) || Right!.Evaluate(atoms, id),
        GraphOp.Not => !Left!.Evaluate(atoms, id),
        _ => false
    };
}

/// <summary>
/// REP-02: Factorized Shared-Condition Graph (FSCG)
/// Stores atomic Boolean conditions once in compact bitmaps and evaluates composite predicates via a DAG.
/// </summary>
public sealed class REP02_FactorizedSharedConditionGraph : IExperimentCandidate, IPredicateEvaluator<long>, ISourceMutationHandler, IMemoryAccountable
{
    public string ExperimentId => "REP-FSCG-002";
    public string CandidateName => "Factorized Shared-Condition Graph (FSCG)";
    public string Family => "Representations";
    public string Description => "Stores 6 atomic condition bitmaps; composite predicates are evaluated via an immutable expression DAG.";

    private readonly EapBooleanBitmap[] _atoms; // 0: active, 1: bal>=100, 2: bal>=250, 3: bal>=500, 4: bal>=750, 5: bal>=900
    private readonly PredicateNode[] _queryDags;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _atoms.Length * _atoms[0].RawMemoryBytes;
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 512;

    public REP02_FactorizedSharedConditionGraph(long maxId)
    {
        _maxId = maxId;
        _atoms = new EapBooleanBitmap[6];
        for (int i = 0; i < 6; i++)
        {
            _atoms[i] = new EapBooleanBitmap(maxId);
        }

        // Build shared DAGs for Q1..Q8
        var nodeActive = new PredicateNode(0);
        var nodeGte100 = new PredicateNode(1);
        var nodeGte250 = new PredicateNode(2);
        var nodeGte500 = new PredicateNode(3);
        var nodeGte750 = new PredicateNode(4);
        var nodeGte900 = new PredicateNode(5);

        _queryDags = new[]
        {
            nodeActive,                                            // Q1
            new PredicateNode(GraphOp.And, nodeActive, nodeGte100), // Q2
            new PredicateNode(GraphOp.And, nodeActive, nodeGte250), // Q3
            new PredicateNode(GraphOp.And, nodeActive, nodeGte500), // Q4
            new PredicateNode(GraphOp.And, nodeActive, nodeGte750), // Q5
            new PredicateNode(GraphOp.And, nodeActive, nodeGte900), // Q6
            nodeGte500,                                            // Q7
            nodeGte100                                             // Q8
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        return _queryDags[predicateIndex].Evaluate(_atoms, id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleMutation(long id, int balance, bool active, long sequenceId)
    {
        _atoms[0].SetEligible(id, active);
        _atoms[1].SetEligible(id, balance >= 100);
        _atoms[2].SetEligible(id, balance >= 250);
        _atoms[3].SetEligible(id, balance >= 500);
        _atoms[4].SetEligible(id, balance >= 750);
        _atoms[5].SetEligible(id, balance >= 900);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        int p = 0;
        for (int i = 0; i < 8; i++)
        {
            if (EvaluatePredicate(id, i)) p |= (1 << (7 - i));
        }
        return (byte)p;
    }
}
