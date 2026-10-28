using System;
using System.Threading.Tasks;
using NeuroCache.EapResearch;
using Xunit;

namespace NeuroCache.EapResearch.Tests;

public class EapCoreTests
{
    [Fact]
    public void EapBooleanBitmap_DirectAddressing_MatchesExpected()
    {
        long maxId = 1000;
        var bitmap = new EapBooleanBitmap(maxId);

        Assert.False(bitmap.IsEligible(1));
        Assert.False(bitmap.IsEligible(64));
        Assert.False(bitmap.IsEligible(65));

        bitmap.SetEligible(1, true);
        bitmap.SetEligible(64, true);
        bitmap.SetEligible(100, true);

        Assert.True(bitmap.IsEligible(1));
        Assert.True(bitmap.IsEligible(64));
        Assert.False(bitmap.IsEligible(65));
        Assert.True(bitmap.IsEligible(100));

        bitmap.SetEligible(64, false);
        Assert.False(bitmap.IsEligible(64));
    }

    [Fact]
    public void EapPackedCategoryPlane_MultiThresholdEvaluation_Correct()
    {
        long maxId = 100;
        var plane = new EapPackedCategoryPlane(maxId);

        // Account 1: balance = 150, active = true (Category ~ 11)
        plane.SetAccount(1, 150, true);

        // Account 2: balance = 50, active = true (Category ~ 3)
        plane.SetAccount(2, 50, true);

        // Account 3: balance = 150, active = false
        plane.SetAccount(3, 150, false);

        // Query threshold 100 (Category ~ 7)
        Assert.True(plane.EvaluateThreshold(1, 100));   // 150 >= 100 AND active -> true
        Assert.False(plane.EvaluateThreshold(2, 100));  // 50 < 100 -> false
        Assert.False(plane.EvaluateThreshold(3, 100));  // active = false -> false

        // Query threshold 25 (Category ~ 1)
        Assert.True(plane.EvaluateThreshold(2, 25));    // 50 >= 25 AND active -> true
    }

    [Fact]
    public async Task EapCoordinator_SynchronizedUpdates_Consistent()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        // Test in-memory bitmap and category updating
        var coord = new EapCoordinator(100, connStr);

        coord.Bitmap.SetEligible(10, true);
        Assert.True(coord.QueryEligible(10));

        coord.Bitmap.SetEligible(10, false);
        Assert.False(coord.QueryEligible(10));

        await coord.DisposeAsync();
    }
}
