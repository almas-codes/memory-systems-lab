using System;
using System.Threading.Tasks;
using Npgsql;
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
        coord.MarkHealthy(true);

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
    public async Task EapCoordinator_CrossPlane_SeqlockConsistency_NoTornReads()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        long maxId = 100;
        var coord = new EapCoordinator(maxId, connStr);
        coord.MarkHealthy(true);

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
        await writer;

        Assert.False(tornReadDetected, "Torn cross-plane read observed! Boolean plane and Category plane fell out of sync.");
    }

    [Fact]
    public async Task Seqlock_ConcurrentWritersOnSameShard_NoTornReads()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        // ID 5 and ID 1029 share shard (5 % 1024 == 5, 1029 % 1024 == 5)
        long maxId = 2048;
        var coord = new EapCoordinator(maxId, connStr);
        coord.MarkHealthy(true);

        // Pre-initialize both accounts to consistent eligible state
        coord.Bitmap.SetEligible(5, true);
        coord.CategoryPlane.SetAccount(5, 150, true);
        coord.Bitmap.SetEligible(1029, true);
        coord.CategoryPlane.SetAccount(1029, 150, true);

        int tornReads = 0;
        var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Two concurrent writers hammering the SAME shard concurrently on different IDs
        var writer1 = Task.Run(() =>
        {
            int i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                bool el = (i++ % 2 == 0);
                coord.PublishCrossPlane(5, el ? 150 : 50, true);
            }
        });

        var writer2 = Task.Run(() =>
        {
            int i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                bool el = (i++ % 2 == 0);
                coord.PublishCrossPlane(1029, el ? 150 : 50, true);
            }
        });

        // Four concurrent readers querying both accounts under heavy concurrent writes
        Parallel.For(0, 4, _ =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var (el5, catEl5) = coord.QueryCrossPlaneConsistent(5, 100);
                if (el5 != catEl5)
                {
                    System.Threading.Interlocked.Increment(ref tornReads);
                }

                var (el1029, catEl1029) = coord.QueryCrossPlaneConsistent(1029, 100);
                if (el1029 != catEl1029)
                {
                    System.Threading.Interlocked.Increment(ref tornReads);
                }
            }
        });

        cts.Cancel();
        await Task.WhenAll(writer1, writer2);

        Assert.True(tornReads == 0, $"Adversarial Seqlock multi-writer test detected {tornReads} torn reads! Shard writes were not properly serialized.");
    }

    [Fact]
    public void CategorySemantics_BoundaryThresholdMatrix()
    {
        // Boundary set: 0, 1, 12, 13, 124, 125, 129, 130, 194, 195, 196, 208
        int[] exactBoundaries = { 0, 13, 130, 195 };
        int[] ambiguousBoundaries = { 1, 12, 124, 125, 129, 194, 196, 208 };

        foreach (var t in exactBoundaries)
        {
            Assert.True(EapPackedCategoryPlane.IsThresholdExact(t), $"Threshold {t} should be recognized as an exact boundary (multiple of 13).");
        }

        foreach (var t in ambiguousBoundaries)
        {
            Assert.False(EapPackedCategoryPlane.IsThresholdExact(t), $"Threshold {t} should be recognized as non-exact / requiring disambiguation.");
        }

        long maxId = 100;
        var plane = new EapPackedCategoryPlane(maxId);

        // Account with balance = 125 (Category = 125 / 13 = 9), active = true
        plane.SetAccount(1, 125, true);

        // 1. Lower thresholds (<= 117): category 9 > reqBucket -> DefiniteTrue
        Assert.Equal(CategoryEvaluationResult.DefiniteTrue, plane.EvaluateThresholdDetailed(1, 0));
        Assert.Equal(CategoryEvaluationResult.DefiniteTrue, plane.EvaluateThresholdDetailed(1, 13));
        Assert.Equal(CategoryEvaluationResult.DefiniteTrue, plane.EvaluateThresholdDetailed(1, 100)); // reqBucket 7 < 9

        // 2. Higher thresholds (>= 130): category 9 < reqBucket 10 -> DefiniteFalse
        Assert.Equal(CategoryEvaluationResult.DefiniteFalse, plane.EvaluateThresholdDetailed(1, 130));
        Assert.Equal(CategoryEvaluationResult.DefiniteFalse, plane.EvaluateThresholdDetailed(1, 195));

        // 3. Same bucket (Bucket 9 covers [117, 129]):
        // Threshold 124: within bucket -> AmbiguousBucket
        Assert.Equal(CategoryEvaluationResult.AmbiguousBucket, plane.EvaluateThresholdDetailed(1, 124));
        // Threshold 125: within bucket -> AmbiguousBucket
        Assert.Equal(CategoryEvaluationResult.AmbiguousBucket, plane.EvaluateThresholdDetailed(1, 125));
        // Threshold 129: within bucket -> AmbiguousBucket
        Assert.Equal(CategoryEvaluationResult.AmbiguousBucket, plane.EvaluateThresholdDetailed(1, 129));
    }

    [Fact]
    public async Task CategorySemantics_CompareAgainstPostgreSqlGroundTruth()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        long maxId = 1_000_000;
        var coord = new EapCoordinator(maxId, connStr, tableName: "eap_lab.accounts_500m");

        // Populate accounts from DB into coordinator
        await coord.Bitmap.BuildFromPostgreSqlAsync(connStr, 100, "eap_lab.accounts_500m");
        await coord.CategoryPlane.BuildFromPostgreSqlAsync(connStr, "eap_lab.accounts_500m");
        coord.MarkHealthy(true);

        // Verify that QueryThreshold with DB fallback produces 100% agreement with direct SQL query
        int[] testThresholds = { 0, 1, 12, 13, 124, 125, 129, 130, 194, 195, 196, 208 };
        await using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync();

        for (int i = 0; i < testThresholds.Length; i++)
        {
            int t = testThresholds[i];
            long testId = (i * 73) % maxId + 1;

            await using var cmd = new Npgsql.NpgsqlCommand("SELECT (balance >= @t AND active) FROM eap_lab.accounts_500m WHERE id = @id;", conn);
            cmd.Parameters.AddWithValue("t", t);
            cmd.Parameters.AddWithValue("id", testId);
            bool dbExpected = (bool)(await cmd.ExecuteScalarAsync())!;

            bool coordinatorResult = coord.QueryThreshold(testId, t);
            Assert.Equal(dbExpected, coordinatorResult);
        }

        await coord.DisposeAsync();
    }

    [Fact]
    public void MemoryAccounting_StructAlignmentAndPadding_Verified()
    {
        int structSize = System.Runtime.CompilerServices.Unsafe.SizeOf<Candidate4_WarrantV4.WarrantEntry>();
        Assert.Equal(16, structSize);

        // 4M entries calculation
        long count = 4_194_304;
        long totalBytes = count * structSize;
        double binaryMiB = totalBytes / (1024.0 * 1024.0);
        double decimalMB = totalBytes / 1_000_000.0;

        Assert.Equal(64.0, binaryMiB, 4);
        Assert.Equal(67.108864, decimalMB, 4);
    }

    [Fact]
    public async Task CacheWarrant_ColdVsWarmIsolation()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        var warrant = new Candidate4_WarrantV4(1024);
        await warrant.InitializeAsync(100, connStr);

        // Cold query: should miss cache and fetch from DB
        bool coldRes = await warrant.QueryEligibleAsync(1, 100);

        // Reset cache explicitly
        warrant.ResetCache();

        // After reset, querying should still return correct ground truth
        bool resetRes = await warrant.QueryEligibleAsync(1, 100);
        Assert.Equal(coldRes, resetRes);

        await warrant.DisposeAsync();
    }

    [Fact]
    public async Task TransactionalOutbox_OutboxSequenceOrdering_AdvisoryLockPreventsGap()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        // Verify advisory lock acquisition and transactional serialization
        await using var conn1 = new Npgsql.NpgsqlConnection(connStr);
        await using var conn2 = new Npgsql.NpgsqlConnection(connStr);
        await conn1.OpenAsync();
        await conn2.OpenAsync();

        await using var tx1 = await conn1.BeginTransactionAsync();
        await using (var cmd1 = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('adversarial_test_lock'));", conn1, tx1))
        {
            await cmd1.ExecuteNonQueryAsync();
        }

        // Connection 2 attempts non-blocking try-lock: must return false because tx1 holds it
        await using (var cmd2 = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtext('adversarial_test_lock'));", conn2))
        {
            bool locked = (bool)(await cmd2.ExecuteScalarAsync())!;
            Assert.False(locked, "Advisory lock should be held by transaction 1, preventing concurrent uncommitted outbox writes.");
        }

        await tx1.RollbackAsync();

        // After tx1 rolls back, conn2 can acquire it
        await using (var cmd3 = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtext('adversarial_test_lock'));", conn2))
        {
            bool locked = (bool)(await cmd3.ExecuteScalarAsync())!;
            Assert.True(locked, "After tx1 finishes, lock should be immediately available.");
        }
    }

    [Fact]
    public async Task EapCoordinator_CrashRecovery_ReconcilePendingMutations()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        long maxId = 100;
        var coord = new EapCoordinator(maxId, connStr, mutationLogTable: "eap_audit_20261028.mutation_log");
        coord.MarkHealthy(true);

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

    [Fact]
    public async Task Hydration_IdBeyondAllocatedMaximum_ThrowsAndMarksUnhealthy()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_overflow";

        await using (var conn = new Npgsql.NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL);
                INSERT INTO {testTable} (id, balance, active) VALUES (1, 150, true), (250, 150, true);", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Capacity allocated is only 100, but row with ID 250 exists
        var coord = new EapCoordinator(100, connStr, tableName: testTable);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await coord.HydrateSynchronizedFromPostgreSqlAsync();
        });

        Assert.Contains("exceeds allocated capacity", ex.Message);
        Assert.False(coord.IsHealthy, "Coordinator must NOT declare healthy when an out-of-bounds row was detected.");

        // Reads must be rejected when coordinator is unhealthy
        Assert.Throws<InvalidOperationException>(() => coord.QueryEligible(1));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Hydration_MissingIdInsideAllocatedRange_HandlesSparsely()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_sparse_missing";

        await using (var conn = new Npgsql.NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL);
                INSERT INTO {testTable} (id, balance, active) VALUES (1, 150, true), (5, 150, true), (20, 150, true);", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var coord = new EapCoordinator(100, connStr, tableName: testTable);
        var (processed, _, _) = await coord.HydrateSynchronizedFromPostgreSqlAsync();

        Assert.Equal(3, processed);
        Assert.True(coord.IsHealthy);

        // Populated IDs are eligible
        Assert.True(coord.QueryEligible(1));
        Assert.True(coord.QueryEligible(5));
        Assert.True(coord.QueryEligible(20));

        // Missing IDs inside the range are safely false
        Assert.False(coord.QueryEligible(2));
        Assert.False(coord.QueryEligible(3));
        Assert.False(coord.QueryEligible(19));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Hydration_EmptyTable_HandledSafely()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_empty";

        await using (var conn = new Npgsql.NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL);", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var coord = new EapCoordinator(100, connStr, tableName: testTable);
        var (processed, _, _) = await coord.HydrateSynchronizedFromPostgreSqlAsync();

        Assert.Equal(0, processed);
        Assert.True(coord.IsHealthy);
        Assert.False(coord.QueryEligible(1));
        Assert.False(coord.QueryEligible(50));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Hydration_SparseIds_CorrectlyIndexed()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_sparse_values";

        await using (var conn = new Npgsql.NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL);
                INSERT INTO {testTable} (id, balance, active) VALUES 
                    (10, 130, true), 
                    (50, 65, true), 
                    (90, 20, false);", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var coord = new EapCoordinator(100, connStr, tableName: testTable);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        Assert.True(coord.IsHealthy);

        // Account 10: balance 130, active -> eligible at threshold 100, and exact at 130
        Assert.True(coord.QueryEligible(10));
        Assert.True(coord.QueryThreshold(10, 130));

        // Account 50: balance 65, active -> not eligible at 100, eligible at 65
        Assert.False(coord.QueryEligible(50));
        Assert.True(coord.QueryThreshold(50, 65));

        // Account 90: active false -> not eligible
        Assert.False(coord.QueryEligible(90));
        Assert.False(coord.QueryThreshold(90, 13));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Hydration_FailedHydrationOperation_LeavesPlaneUnhealthy()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string nonExistentTable = "eap_audit_20261028.non_existent_table_xyz_123";

        var coord = new EapCoordinator(100, connStr, tableName: nonExistentTable);

        await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            await coord.HydrateSynchronizedFromPostgreSqlAsync();
        });

        Assert.False(coord.IsHealthy);
        Assert.Throws<InvalidOperationException>(() => coord.QueryEligible(1));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Hydration_CancellationDuringHydration_AbortsAndLeavesUnhealthy()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        var coord = new EapCoordinator(100, connStr, tableName: "eap_lab.accounts_500m");

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await coord.HydrateSynchronizedFromPostgreSqlAsync(ct: cts.Token);
        });

        Assert.False(coord.IsHealthy);
        Assert.Throws<InvalidOperationException>(() => coord.QueryEligible(1));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Hydration_MutationsAcrossAllPhases_CorrectlyReflectedInPlane()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_phases_accounts";
        string testOutbox = "eap_audit_20261028.test_phases_outbox";

        await using (var setupConn = new Npgsql.NpgsqlConnection(connStr))
        {
            await setupConn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable};
                DROP TABLE IF EXISTS {testOutbox};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL, version BIGINT NOT NULL DEFAULT 1);
                CREATE TABLE {testOutbox} (seq_id BIGSERIAL PRIMARY KEY, account_id BIGINT NOT NULL, balance INT NOT NULL, active BOOLEAN NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp());
                INSERT INTO {testTable} (id, balance, active) 
                SELECT s, 50, true FROM generate_series(1, 10) s;
            ", setupConn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Phase 1 mutation: immediately BEFORE hydration starts
        await using (var mutConn = new Npgsql.NpgsqlConnection(connStr))
        {
            await mutConn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                UPDATE {testTable} SET balance = 150 WHERE id = 1;
                INSERT INTO {testOutbox} (account_id, balance, active) VALUES (1, 150, true);
            ", mutConn);
            await cmd.ExecuteNonQueryAsync();
        }

        var coord = new EapCoordinator(10, connStr, defaultThreshold: 100, tableName: testTable, mutationLogTable: testOutbox);

        var phase2Executed = false;
        var phase3Executed = false;

        // Perform synchronized hydration with phase callbacks
        await coord.HydrateSynchronizedFromPostgreSqlAsync(
            onSnapshotAcquired: async (watermark) =>
            {
                // Phase 2: Mutation immediately after snapshot isolation watermark is acquired
                await using var conn2 = new Npgsql.NpgsqlConnection(connStr);
                await conn2.OpenAsync();
                await using var cmd2 = new Npgsql.NpgsqlCommand($@"
                    UPDATE {testTable} SET balance = 150 WHERE id = 2;
                    INSERT INTO {testOutbox} (account_id, balance, active) VALUES (2, 150, true);
                ", conn2);
                await cmd2.ExecuteNonQueryAsync();
                phase2Executed = true;
            },
            onRowProcessed: async (processedCount) =>
            {
                // Phase 3: Mutation during table streaming (at row 5)
                if (processedCount == 5 && !phase3Executed)
                {
                    await using var conn3 = new Npgsql.NpgsqlConnection(connStr);
                    await conn3.OpenAsync();
                    await using var cmd3 = new Npgsql.NpgsqlCommand($@"
                        UPDATE {testTable} SET balance = 150 WHERE id = 3;
                        INSERT INTO {testOutbox} (account_id, balance, active) VALUES (3, 150, true);
                    ", conn3);
                    await cmd3.ExecuteNonQueryAsync();
                    phase3Executed = true;
                }
            });

        Assert.True(phase2Executed);
        Assert.True(phase3Executed);
        Assert.True(coord.IsHealthy);

        // Phase 4: Mutation after hydration completes and coordinator is healthy
        bool phase4Ok = await coord.UpdateAccountAsync(4, 150, true);
        Assert.True(phase4Ok);

        // Verify: IDs 1, 2, 3, 4 must ALL be eligible (balance = 150 >= 100)
        Assert.True(coord.QueryEligible(1), "Phase 1 mutation (pre-hydration) missed!");
        Assert.True(coord.QueryEligible(2), "Phase 2 mutation (post-snapshot watermark) missed!");
        Assert.True(coord.QueryEligible(3), "Phase 3 mutation (mid-stream) missed!");
        Assert.True(coord.QueryEligible(4), "Phase 4 mutation (post-hydration) missed!");

        // Unmodified accounts (5..10) must remain ineligible
        for (long id = 5; id <= 10; id++)
        {
            Assert.False(coord.QueryEligible(id), $"Account {id} should be ineligible.");
        }

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Hydration_WriterHoldingLockDuringSnapshot_WaitsThenCatchesUp()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_lock_accounts";
        string testOutbox = "eap_audit_20261028.test_lock_outbox";

        await using (var setupConn = new Npgsql.NpgsqlConnection(connStr))
        {
            await setupConn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable};
                DROP TABLE IF EXISTS {testOutbox};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL, version BIGINT NOT NULL DEFAULT 1);
                CREATE TABLE {testOutbox} (seq_id BIGSERIAL PRIMARY KEY, account_id BIGINT NOT NULL, balance INT NOT NULL, active BOOLEAN NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp());
                INSERT INTO {testTable} (id, balance, active) VALUES (5, 50, true);
            ", setupConn);
            await cmd.ExecuteNonQueryAsync();
        }

        var coord = new EapCoordinator(10, connStr, defaultThreshold: 100, tableName: testTable, mutationLogTable: testOutbox);

        var writerStarted = new TaskCompletionSource();
        var snapshotStarted = new TaskCompletionSource();

        // Writer task holds a transaction open on account 5
        var writerTask = Task.Run(async () =>
        {
            await using var writerConn = new Npgsql.NpgsqlConnection(connStr);
            await writerConn.OpenAsync();
            await using var tx = await writerConn.BeginTransactionAsync();

            await using var updateCmd = new NpgsqlCommand($@"
                UPDATE {testTable} SET balance = 180 WHERE id = 5;
                INSERT INTO {testOutbox} (account_id, balance, active) VALUES (5, 180, true);
            ", writerConn, tx);
            await updateCmd.ExecuteNonQueryAsync();

            writerStarted.SetResult();
            await snapshotStarted.Task; // Hold until hydration begins snapshot acquisition

            await tx.CommitAsync();
        });

        await writerStarted.Task;

        // Start hydration
        await coord.HydrateSynchronizedFromPostgreSqlAsync(
            onSnapshotAcquired: async (watermark) =>
            {
                // Release writer transaction commit
                snapshotStarted.TrySetResult();
                await writerTask;
            });

        Assert.True(coord.IsHealthy);
        Assert.True(coord.QueryEligible(5), "Account 5 should reflect committed balance 180.");

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Outbox_TransactionRollback_DoesNotLeaveStaleStateOrCorruptPlanes()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_rb_accounts";
        string testOutbox = "eap_audit_20261028.test_rb_outbox";

        await using (var setupConn = new Npgsql.NpgsqlConnection(connStr))
        {
            await setupConn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable};
                DROP TABLE IF EXISTS {testOutbox};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL, version BIGINT NOT NULL DEFAULT 1);
                CREATE TABLE {testOutbox} (seq_id BIGSERIAL PRIMARY KEY, account_id BIGINT NOT NULL, balance INT NOT NULL, active BOOLEAN NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp());
                INSERT INTO {testTable} (id, balance, active) VALUES (7, 50, true);
            ", setupConn);
            await cmd.ExecuteNonQueryAsync();
        }

        var coord = new EapCoordinator(10, connStr, defaultThreshold: 100, tableName: testTable, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        Assert.False(coord.QueryEligible(7));

        // An uncommitted or rolled-back transaction
        await using (var txConn = new Npgsql.NpgsqlConnection(connStr))
        {
            await txConn.OpenAsync();
            await using var tx = await txConn.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand($@"
                UPDATE {testTable} SET balance = 199 WHERE id = 7;
                INSERT INTO {testOutbox} (account_id, balance, active) VALUES (7, 199, true);
            ", txConn, tx);
            await cmd.ExecuteNonQueryAsync();

            // Explicit rollback
            await tx.RollbackAsync();
        }

        // Reconcile pending mutations: must find 0 committed rows
        int replayed = await coord.ReconcilePendingMutationsAsync();
        Assert.Equal(0, replayed);

        // Account 7 must remain ineligible
        Assert.False(coord.QueryEligible(7), "Rolled back transaction corrupted the in-memory plane!");

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Outbox_RapidSameAccountUpdates_ReplaysInStrictSequenceOrder()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_rapid_accounts";
        string testOutbox = "eap_audit_20261028.test_rapid_outbox";

        await using (var setupConn = new Npgsql.NpgsqlConnection(connStr))
        {
            await setupConn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable};
                DROP TABLE IF EXISTS {testOutbox};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL, version BIGINT NOT NULL DEFAULT 1);
                CREATE TABLE {testOutbox} (seq_id BIGSERIAL PRIMARY KEY, account_id BIGINT NOT NULL, balance INT NOT NULL, active BOOLEAN NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp());
                INSERT INTO {testTable} (id, balance, active) VALUES (8, 50, true);
            ", setupConn);
            await cmd.ExecuteNonQueryAsync();
        }

        var coord = new EapCoordinator(10, connStr, defaultThreshold: 100, tableName: testTable, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        // Perform 4 rapid mutations in sequence on Account 8:
        // 1. balance 150 (eligible)
        // 2. balance 40 (ineligible)
        // 3. balance 190 (eligible)
        // 4. balance 25 (ineligible)
        int[] balances = { 150, 40, 190, 25 };
        await using (var conn = new Npgsql.NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            foreach (var b in balances)
            {
                await using var cmd = new NpgsqlCommand($@"
                    UPDATE {testTable} SET balance = @b WHERE id = 8;
                    INSERT INTO {testOutbox} (account_id, balance, active) VALUES (8, @b, true);
                ", conn);
                cmd.Parameters.AddWithValue("b", b);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        int replayed = await coord.ReconcilePendingMutationsAsync();
        Assert.Equal(4, replayed);

        // Final balance was 25 -> ineligible!
        Assert.False(coord.QueryEligible(8), "Outbox replay did not apply mutations in strict ascending seq_id order!");
        Assert.False(coord.QueryThreshold(8, 50));

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task ExternalMutation_RowLevelTrigger_CapturesDirectSqlWrites()
    {
        string connStr = PostgreSqlPreflight.GetConnectionString();
        string testTable = "eap_audit_20261028.test_trg_accounts";
        string testOutbox = "eap_audit_20261028.test_trg_outbox";

        await using (var setupConn = new Npgsql.NpgsqlConnection(connStr))
        {
            await setupConn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                CREATE SCHEMA IF NOT EXISTS eap_audit_20261028;
                DROP TABLE IF EXISTS {testTable} CASCADE;
                DROP TABLE IF EXISTS {testOutbox};
                CREATE TABLE {testTable} (id BIGINT PRIMARY KEY, balance INT NOT NULL, active BOOLEAN NOT NULL, version BIGINT NOT NULL DEFAULT 1);
                CREATE TABLE {testOutbox} (seq_id BIGSERIAL PRIMARY KEY, account_id BIGINT NOT NULL, balance INT NOT NULL, active BOOLEAN NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp());
                INSERT INTO {testTable} (id, balance, active) VALUES (9, 30, true);

                CREATE OR REPLACE FUNCTION eap_audit_20261028.fn_accounts_audit_trigger()
                RETURNS TRIGGER AS $$
                BEGIN
                    INSERT INTO {testOutbox} (account_id, balance, active)
                    VALUES (NEW.id, NEW.balance, NEW.active);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_test_accounts_mutation ON {testTable};
                CREATE TRIGGER trg_test_accounts_mutation
                AFTER INSERT OR UPDATE ON {testTable}
                FOR EACH ROW EXECUTE FUNCTION eap_audit_20261028.fn_accounts_audit_trigger();
            ", setupConn);
            await cmd.ExecuteNonQueryAsync();
        }

        var coord = new EapCoordinator(10, connStr, defaultThreshold: 100, tableName: testTable, mutationLogTable: testOutbox);
        await coord.HydrateSynchronizedFromPostgreSqlAsync();

        Assert.False(coord.QueryEligible(9));

        // External update simulating an external microservice or direct psql
        await using (var extConn = new Npgsql.NpgsqlConnection(connStr))
        {
            await extConn.OpenAsync();
            await using var extCmd = new NpgsqlCommand($"UPDATE {testTable} SET balance = 165 WHERE id = 9;", extConn);
            await extCmd.ExecuteNonQueryAsync();
        }

        // Outbox should have captured this row via trigger
        int replayed = await coord.ReconcilePendingMutationsAsync();
        Assert.Equal(1, replayed);

        // In-memory plane must reflect the external mutation
        Assert.True(coord.QueryEligible(9), "Trigger-captured external mutation was not reflected in EAP memory!");

        await coord.DisposeAsync();
    }
}
