using NeuroCache.ResearchLab.Recovery;
using Xunit;

namespace NeuroCache.ResearchLab.RecoveryTests;

public class RepresentationRecoveryTests
{
    [Fact]
    public void TestCrashBeforePublicationRecovery()
    {
        var result = REC04_IntegratedFailureRecoveryHarness.RunCrashBeforePublicationTest(accountCount: 1000);
        Assert.True(result.Success, $"Crash recovery failed: {result.Details}");
        Assert.Equal(0, result.MismatchesAgainstOracle);
        Assert.Equal(50, result.RestoredWatermark);
    }

    [Fact]
    public void TestRollbackGapRecovery()
    {
        var result = REC04_IntegratedFailureRecoveryHarness.RunRollbackGapRecoveryTest(accountCount: 500);
        Assert.True(result.Success, $"Rollback gap recovery failed: {result.Details}");
        Assert.Equal(0, result.MismatchesAgainstOracle);
        Assert.Equal(4, result.ReplayedMutations);
    }
}
