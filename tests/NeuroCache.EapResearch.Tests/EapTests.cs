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

    [Fact]
    public void EapBooleanBitmap_BoundaryConditions_HandledCorrectly()
    {
        long maxId = 128;
        var bitmap = new EapBooleanBitmap(maxId);

        // Negative and 0 IDs must return false safely without exception
        Assert.False(bitmap.IsEligible(0));
        Assert.False(bitmap.IsEligible(-1));
        Assert.False(bitmap.IsEligible(-999));

        // Upper out-of-bounds must return false safely
        Assert.False(bitmap.IsEligible(maxId + 1));
        Assert.False(bitmap.IsEligible(maxId + 1000));

        // Exact boundary word transitions
        bitmap.SetEligible(1, true);
        bitmap.SetEligible(64, true);  // End of Word 0
        bitmap.SetEligible(65, true);  // Start of Word 1
        bitmap.SetEligible(128, true); // End of Word 1

        Assert.True(bitmap.IsEligible(1));
        Assert.True(bitmap.IsEligible(64));
        Assert.True(bitmap.IsEligible(65));
        Assert.True(bitmap.IsEligible(128));

        // Non-set bits remain false
        Assert.False(bitmap.IsEligible(2));
        Assert.False(bitmap.IsEligible(63));
        Assert.False(bitmap.IsEligible(66));
        Assert.False(bitmap.IsEligible(127));
    }

    [Fact]
    public async Task EapPackedCategoryPlane_HydrateFromPostgreSql_MatchesOracle()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        long maxId = 1_000_000;
        var plane = new EapPackedCategoryPlane(maxId);

        // Hydrate from PostgreSQL authoritative table
        var duration = await plane.BuildFromPostgreSqlAsync(connStr, "eap_lab.accounts_500m");
        Assert.True(duration.TotalMilliseconds > 0);

        // Sample 500 IDs across the 1,000,000 range and compare with PostgreSQL
        await using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var rng = new Random(1337);
        for (int i = 0; i < 500; i++)
        {
            long testId = rng.NextInt64(1, maxId + 1);
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT balance, active FROM eap_lab.accounts_500m WHERE id = @id;", conn);
            cmd.Parameters.AddWithValue("id", testId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            int dbBalance = reader.GetInt32(0);
            bool dbActive = reader.GetBoolean(1);

            // Test against discrete category tier boundaries (e.g. Category 8 = 104)
            int targetCategory = (i % 15) + 1;
            int testThreshold = targetCategory * 13;

            byte expectedCat = EapPackedCategoryPlane.BalanceToCategory(dbBalance);
            bool expectedEligible = (expectedCat >= targetCategory) && dbActive;
            bool planeEligible = plane.EvaluateThreshold(testId, testThreshold);

            Assert.Equal(expectedEligible, planeEligible);
        }
    }

    [Fact]
    public void EapCoordinator_CrossPlane_SeqlockConsistency_NoTornReads()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        long maxId = 100;
        var coord = new EapCoordinator(maxId, connStr);

        // Initialize account 10 as eligible (balance = 150, active = true)
        coord.Bitmap.SetEligible(10, true);
        coord.CategoryPlane.SetAccount(10, 150, true);

        bool tornReadDetected = false;
        var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Parallel writer toggling account 10 between eligible (150) and ineligible (50) under Seqlock
        var writer = Task.Run(() =>
        {
            int iter = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                bool eligible = (iter++ % 2 == 0);
                int bal = eligible ? 150 : 50;

                // Atomic multi-plane publication under Seqlock
                coord.PublishCrossPlane(10, bal, true);
            }
        });

        // Parallel readers evaluating consistency
        Parallel.For(0, 4, _ =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var (el, catEl) = coord.QueryCrossPlaneConsistent(10, 100);
                // At threshold 100: balance 150 -> (true, true); balance 50 -> (false, false)
                // A torn read would be (true, false) or (false, true)
                if (el != catEl)
                {
                    tornReadDetected = true;
                    break;
                }
            }
        });

        cts.Cancel();
        writer.Wait();

        Assert.False(tornReadDetected, "Torn cross-plane read observed! Boolean plane and Category plane fell out of sync.");
    }

    [Fact]
    public async Task EapCoordinator_CrashRecovery_ReconcilePendingMutations()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        long maxId = 100;
        var coord = new EapCoordinator(maxId, connStr, mutationLogTable: "eap_audit_20261028.mutation_log");

        // Initial state for account 42: balance = 50 (ineligible)
        coord.Bitmap.SetEligible(42, false);
        coord.CategoryPlane.SetAccount(42, 50, true);
        Assert.False(coord.QueryEligible(42));

        // Simulate crash: mutation committed in PostgreSQL log, but process crashed before in-memory update
        await using (var conn = new Npgsql.NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(@"
                INSERT INTO eap_audit_20261028.mutation_log (account_id, balance, active) 
                VALUES (42, 180, true);", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Reconcile and replay
        int replayed = await coord.ReconcilePendingMutationsAsync();
        Assert.True(replayed > 0);

        // Memory plane must now reflect the replayed mutation
        Assert.True(coord.QueryEligible(42));
        Assert.True(coord.QueryThreshold(42, 100));

        await coord.DisposeAsync();
    }
}
