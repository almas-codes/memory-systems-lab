using System;
using System.Collections.Generic;
using System.Linq;

namespace NeuroCache.ResearchLab.DynamicSchema;

public sealed record QueryPredicate(
    int Id,
    string Name,
    string SqlPredicate,
    Func<int, bool, bool> Evaluator);

public sealed class VersionedQueryFamily
{
    public int Version { get; }
    public IReadOnlyList<QueryPredicate> Predicates { get; }
    public int QueryCount => Predicates.Count;

    public VersionedQueryFamily(int version, IEnumerable<QueryPredicate> predicates)
    {
        Version = version;
        Predicates = predicates.ToList().AsReadOnly();
    }

    public bool EvaluatePredicate(int queryIndex, int balance, bool active)
    {
        if (queryIndex < 0 || queryIndex >= Predicates.Count)
            throw new ArgumentOutOfRangeException(nameof(queryIndex), $"Index {queryIndex} out of range for version {Version} (count {Predicates.Count})");

        return Predicates[queryIndex].Evaluator(balance, active);
    }

    public uint ComputeAnswerVector(int balance, bool active)
    {
        uint vector = 0;
        for (int i = 0; i < Predicates.Count; i++)
        {
            if (Predicates[i].Evaluator(balance, active))
            {
                vector |= (1U << (Predicates.Count - 1 - i));
            }
        }
        return vector;
    }

    public static VersionedQueryFamily CreateV1Baseline()
    {
        var predicates = new List<QueryPredicate>
        {
            new(0, "Q1_Active", "active", (b, a) => a),
            new(1, "Q2_ActiveAndBalGte100", "(balance >= 100 AND active)", (b, a) => a && b >= 100),
            new(2, "Q3_ActiveAndBalGte250", "(balance >= 250 AND active)", (b, a) => a && b >= 250),
            new(3, "Q4_ActiveAndBalGte500", "(balance >= 500 AND active)", (b, a) => a && b >= 500),
            new(4, "Q5_ActiveAndBalGte750", "(balance >= 750 AND active)", (b, a) => a && b >= 750),
            new(5, "Q6_ActiveAndBalGte900", "(balance >= 900 AND active)", (b, a) => a && b >= 900),
            new(6, "Q7_BalGte500", "(balance >= 500)", (b, a) => b >= 500),
            new(7, "Q8_BalGte100", "(balance >= 100)", (b, a) => b >= 100)
        };
        return new VersionedQueryFamily(1, predicates);
    }

    public VersionedQueryFamily AddPredicate(string name, string sql, Func<int, bool, bool> evaluator)
    {
        var nextList = new List<QueryPredicate>(Predicates)
        {
            new(Predicates.Count, name, sql, evaluator)
        };
        return new VersionedQueryFamily(Version + 1, nextList);
    }

    public VersionedQueryFamily RemovePredicate(int id)
    {
        var nextList = Predicates.Where(p => p.Id != id).ToList();
        return new VersionedQueryFamily(Version + 1, nextList);
    }
}
