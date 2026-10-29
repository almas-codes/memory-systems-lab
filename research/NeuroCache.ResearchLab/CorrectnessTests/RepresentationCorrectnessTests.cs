using System.Collections.Generic;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.Data;
using NeuroCache.ResearchLab.HybridCompositions;
using NeuroCache.ResearchLab.MemoryAndLocality;
using NeuroCache.ResearchLab.MutationMaintenance;
using NeuroCache.ResearchLab.Oracle;
using NeuroCache.ResearchLab.Recovery;
using NeuroCache.ResearchLab.Representations;
using NeuroCache.ResearchLab.SemanticEncoding;
using NeuroCache.ResearchLab.ThresholdEncoding;
using Xunit;

namespace NeuroCache.ResearchLab.CorrectnessTests;

public class RepresentationCorrectnessTests
{
    private const int TestAccountCount = 2000;
    private readonly PureCodeOracle _oracle;

    public RepresentationCorrectnessTests()
    {
        _oracle = new PureCodeOracle();
        for (int i = 0; i < TestAccountCount; i++)
        {
            int bal = (i * 37) % 1500;
            bool act = (i % 3) != 0;
            _oracle.Upsert(i, bal, act, 1);
        }
    }

    [Fact]
    public void REP01_CorrelatedOutcomeJointBitmap_MatchesOracle()
    {
        var rep = new REP01_CorrelatedOutcomeJointBitmap(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void REP02_FactorizedSharedConditionGraph_MatchesOracle()
    {
        var rep = new REP02_FactorizedSharedConditionGraph(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void SEM01_IndependentBitmaps_MatchesOracle()
    {
        var rep = new SEM01_IndependentBitmaps(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void SEM02_ByteAnswerVector_MatchesOracle()
    {
        var rep = new SEM02_ByteAnswerVector(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void SEM03_FourBitSemanticClasses_MatchesOracle()
    {
        var rep = new SEM03_FourBitSemanticClasses(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void SEM04_ThreeBitSemanticClasses_MatchesOracle()
    {
        var rep = new SEM04_ThreeBitSemanticClasses(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void REC01_CopyOnWriteAnswerPages_MatchesOracle()
    {
        var rep = new REC01_CopyOnWriteAnswerPages(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void REC02_CheckpointedContiguousPrefixReplay_MatchesOracle()
    {
        var rep = new REC02_CheckpointedContiguousPrefixReplay(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void REC03_SeqlockPagePublication_MatchesOracle()
    {
        var rep = new REC03_SeqlockPagePublication(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void COMBO01_FactorizedAtomsWithDarg_MatchesOracle()
    {
        var rep = new COMBO01_FactorizedAtomsWithDargAndFreshness(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void COMBO02_SemanticClassesWithRareExceptions_MatchesOracle()
    {
        var rep = new COMBO02_SemanticClassesWithRareExceptions(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void COMBO03_BoundedOverlayWithCow_MatchesOracle()
    {
        var rep = new COMBO03_BoundedOverlayWithCowGenerations(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void COMBO04_SharedAtomsWithBatchReuse_MatchesOracle()
    {
        var rep = new COMBO04_SharedAtomsWithBatchIntermediateReuse(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void COMBO05_HotMaterializationWithExactColdFallback_MatchesOracle()
    {
        var rep = new COMBO05_HotMaterializationWithExactColdFallback(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    [Fact]
    public void COMBO06_CompletePortfolioRouter_MatchesOracle()
    {
        var rep = new COMBO06_CompletePortfolioRouter(TestAccountCount);
        LoadData(rep);
        VerifyAgainstOracle(rep);
    }

    private void LoadData(ISourceMutationHandler rep)
    {
        for (int i = 0; i < TestAccountCount; i++)
        {
            var rec = _oracle.GetRecord(i)!.Value;
            rep.HandleMutation(i, rec.Balance, rec.Active, 1);
        }
    }

    private void VerifyAgainstOracle(IPredicateEvaluator<long> evaluator)
    {
        for (int i = 0; i < TestAccountCount; i++)
        {
            for (int p = 0; p < 8; p++)
            {
                bool cached = evaluator.EvaluatePredicate(i, p);
                bool expected = _oracle.Evaluate(i, p);
                Assert.True(cached == expected, $"Mismatch for account {i}, predicate {p}. Expected {expected}, got {cached}");
            }
        }
    }
}
