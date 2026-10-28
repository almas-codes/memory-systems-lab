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

    [Fact]
    public void EapBooleanBitmap_ConcurrentUpdates_SameWord_NoUpdatesLost()
    {
        // 64 accounts sharing the exact same 64-bit word (IDs 1 to 64)
        long maxId = 64;
        var bitmap = new EapBooleanBitmap(maxId);

        // Run 64 concurrent tasks, each repeatedly toggling and finally setting their assigned bit
        int iterations = 10_000;
        Parallel.For(1, 65, id =>
        {
            for (int i = 0; i < iterations; i++)
            {
                bitmap.SetEligible(id, (i & 1) == 0);
            }
            // Deterministic final state: even IDs -> true, odd IDs -> false
            bitmap.SetEligible(id, id % 2 == 0);
        });

        // Verify all 64 bits in the single 64-bit word
        for (long id = 1; id <= 64; id++)
        {
            bool expected = (id % 2 == 0);
            Assert.True(bitmap.IsEligible(id) == expected, $"Bit for ID {id} was lost or corrupted during concurrent updates in the same 64-bit word.");
        }
    }

    [Fact]
    public void EapPackedCategoryPlane_ConcurrentNibbles_SameByte_NoUpdatesLost()
    {
        // 100 accounts -> 50 shared bytes (2 accounts per byte)
        long maxId = 100;
        var plane = new EapPackedCategoryPlane(maxId);

        int iterations = 10_000;
        Parallel.For(1, 101, id =>
        {
            for (int i = 0; i < iterations; i++)
            {
                plane.SetAccount(id, (i % 15) * 13, (i & 1) == 0);
            }
            // Deterministic final state: balance = id * 1.5, active = true
            int finalBalance = (int)(id * 1.5);
            plane.SetAccount(id, finalBalance, true);
        });

        for (long id = 1; id <= 100; id++)
        {
            int expectedBalance = (int)(id * 1.5);
            byte expectedCat = EapPackedCategoryPlane.BalanceToCategory(expectedBalance);
            // Verify threshold matching expected category
            int testThreshold = expectedCat * 13;
            Assert.True(plane.EvaluateThreshold(id, testThreshold), $"Category for ID {id} was corrupted by adjacent nibble concurrent write.");
        }
    }
}
