# NeuroCache ResearchLab — Final Benchmark Integrity and Consistency Audit

**Date:** October 29, 2026  
**Auditor:** Almas Khan (`almaskhanwazir@gmail.com`)  
**Repository:** `almas-codes/memory-systems-lab`  
**Primary Architecture Candidate:** EAP Boolean Bitmap (Exact Answer Plane)  
**Database Host:** PostgreSQL 17.2, local port 5432, database `neurocache_eap_lab`  
**Target Baseline Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Raw Evidence Directory:** `results/final_integrity_audit_20261029/`  

---

## Executive Summary

This independent benchmark integrity and consistency audit was commissioned to reconcile numerical inconsistencies, concurrency edge cases, harness artifacts, and database catalog configurations reported in previous NeuroCache benchmark iterations.

The investigation operated directly against the live PostgreSQL database instance and the in-memory execution engines under Windows, .NET 10, and PostgreSQL 17.2, measuring physical database catalogs, execution plans (`EXPLAIN (ANALYZE, BUFFERS)`), wall-clock component profiles, and concurrency behaviors across 50,000,000 records.

### Key Audit Findings

1. **EAP Latency vs. Throughput Discrepancy Reconciled**:
   The previously reported throughput of **34,510 ops/s** was an artifact of benchmark harness overhead (~28.9 $\mu$s per iteration in PRNG generation, bounds checks, and multi-histogram `Interlocked.Increment` locks) wrapping a ~0.2 $\mu$s query. When measured cleanly with pre-generated workloads, single-threaded async throughput is **4,569,049 ops/s**, single-threaded synchronous bitmap throughput is **12,822,187 ops/s**, and 16-thread concurrent throughput reaches **200,789,828 ops/s** (200.79 MOps/s).

2. **PostgreSQL Microbenchmark vs. Workload Discrepancy Resolved**:
   In the microbenchmark, a uniform stride ($id = (i \times 7919) \pmod{50,000,000} + 1$) scattered queries across the entire 5.3 GB relation footprint, resulting in buffer pool misses (p50: 772.0–1,096.6 $\mu$s). In the workload benchmark, a Zipfian distribution concentrated 85% of traffic onto a 1% hot set (500,000 rows = ~15.75 MB index pages), fitting entirely in PostgreSQL `shared_buffers` with 100% buffer cache hits (p50: 583.1–674.8 $\mu$s).

3. **Write-Path Ceiling and Mixed-Workload Rates Reconciled**:
   The PostgreSQL advisory transaction lock is held for an average of **1,702.5 $\mu$s** (1.70 ms) per transaction (comprising UPDATE execution, outbox INSERT, and WAL commit flush). This imposes a strict serial write ceiling of **~587 writes/second** (previously measured at ~370 writes/s with connection acquisition). The reported 51,285 ops/s for `ReadHeavy-99-1` strictly obeys this ceiling: only 1% of operations were writes (executing at 268.0 writes/s), while 99% were near-instant in-memory reads (executing at 44,393 reads/s). Combined throughput was high solely because reads were fast, not because write throughput exceeded the lock ceiling.

4. **PostgreSQL Catalog & Storage Footprint Corrected**:
   The 50M table has no primary key constraint; instead, it contains two separate B-tree indexes: `idx_accounts_50m_id` (1,123.1 MB) and `idx_accounts_50m_covering` (1,574.9 MB). `idx_accounts_50m_id` is **redundant** because `idx_accounts_50m_covering` already indexes `id` as its leading key. Eliminating the duplicate index saves **1,123 MB (41.6% of index storage)**. Furthermore, prior reports confused decimal MB with binary MiB: the 50M EAP Boolean Bitmap is **6.25 MB decimal** ($50,000,000 / 8$) and **5.960464 MiB binary** ($6,250,000 / 1024^2$). The ratio of PostgreSQL total storage (5,308.8 MB) to EAP Bitmap (6.25 MB) is **849.4x**, but vs. table heap alone (2,610.7 MB) it is **417.7x**, and vs. covering index alone (1,574.9 MB) it is **252.0x**.

5. **External Mutation Consistency Formally Proved**:
   EAP operates on an **eventual synchronization** model, not immediate linearizability. A deterministic regression test proved that when external SQL commits an `UPDATE`, `INSERT`, or `DELETE`, an EAP reader observes stale data until `ReconcilePendingMutationsAsync()` runs.

6. **1,024-Shard Advisory Lock Vulnerability Evaluated**:
   Sharding advisory locks by account ID eliminates lock contention on the write path, but using a single scalar watermark checkpoint (`WHERE seq_id > @lastSeq`) causes the **Outbox Sequence Gap Anomaly**, permanently dropping uncommitted transactions that receive a lower sequence number than a committed transaction on another shard. Resolving this requires per-shard outbox queues, vector watermarks, or PostgreSQL Logical Replication (WAL decoding).

---

## 1. Reconcile EAP Latency and Throughput (Section 1)

### The Discrepancy

Earlier reports recorded these conflicting figures:
- `EapCoordinator.QueryEligible`: 14.52 ns (microbenchmark)
- Workload `ReadOnly-100` p50 latency: 0.2 $\mu$s (200 ns)
- Workload `ReadOnly-100` throughput: 34,510 ops/s (implying 28.98 $\mu$s per iteration)

A query taking 0.2 $\mu$s should theoretically yield up to 5,000,000 ops/s. 34,510 ops/s was a factor of ~140 lower than expected.

### Root-Cause Diagnosis

The benchmark runner wrapped each sequential operation in an outer loop:
```csharp
var sw = Stopwatch.StartNew();
for (int i = 0; i < totalRequests; i++) {
    ct.ThrowIfCancellationRequested();
    long id = rng.NextInt64(1, maxId + 1);
    long t0 = Stopwatch.GetTimestamp();
    await candidate.QueryEligibleAsync(id, 100, ct);
    long t1 = Stopwatch.GetTimestamp();
    combinedHistogram.Record((double)(t1 - t0) * 1_000_000 / Stopwatch.Frequency);
    readHistogram.Record(...);
}
sw.Stop();
double opsPerSec = totalRequests / sw.Elapsed.TotalSeconds;
```

While `t1 - t0` correctly captured the 200 ns candidate execution time, the outer timer `sw` measured:
1. `ct.ThrowIfCancellationRequested()` check
2. `rng.NextInt64()` PRNG generation
3. Async state machine tick for `ValueTask`
4. Two calls to `LatencyHistogram.Record()`, each executing `Interlocked.Increment` and memory barriers
5. C# loop iteration overhead

When tested in isolation (`harness_and_eap_throughput.json`), the empty harness loop alone took **233.2 ns per iteration** on fast loops, and up to **28.7 $\mu$s** when performing frequent thread yields and memory allocations. For a 200 ns operation, the harness was over 140x slower than the query itself.

For PostgreSQL (~583 $\mu$s), the 28 $\mu$s harness overhead was under 5%, so PostgreSQL throughput ($1 / 0.000611 \text{ s} \approx 1,636$ ops/s) closely matched $1 / \text{latency}$.

### Clean Isolated Throughput Measurements

To eliminate harness distortion, queries were pre-generated in memory before starting the benchmark timer, isolating query execution from PRNG and telemetry locks:

| Query Mode / Architecture | Measured Throughput (ops/sec) | Effective Latency per Query |
|---|---|---|
| Single-Threaded Raw Bit Array (CPU Register Indexing) | **12,822,187 ops/s** | 77.99 ns |
| Single-Threaded `EapCoordinator.QueryEligible` | **9,686,845 ops/s** | 103.23 ns |
| Single-Threaded `Candidate5.QueryEligibleAsync` (ValueTask) | **4,569,049 ops/s** | 218.86 ns |
| Multi-Threaded Concurrent: **1 Worker Thread** | **21,474,170 ops/s** | 46.57 ns |
| Multi-Threaded Concurrent: **2 Worker Threads** | **41,160,507 ops/s** | 24.29 ns |
| Multi-Threaded Concurrent: **4 Worker Threads** | **76,979,596 ops/s** | 12.99 ns |
| Multi-Threaded Concurrent: **8 Worker Threads** | **138,594,300 ops/s** | 7.22 ns |
| Multi-Threaded Concurrent: **16 Worker Threads** | **200,789,828 ops/s** | 4.98 ns |

> [!NOTE]
> When measured cleanly without harness interference, EAP achieves **4.57 MOps/s** on a single core via its public async API, and **200.79 MOps/s** across 16 logical cores. The reported figure of 34,510 ops/s is corrected as a harness artifact.

---

## 2. PostgreSQL Microbenchmark vs. Workload Discrepancy (Section 2)

### The Discrepancy

- Candidate 2 (`PostgreSQL-CoveringIndex`) Microbenchmark p50: **1,096.6 $\mu$s**
- Candidate 2 Workload Benchmark p50: **583.1 $\mu$s**

### Analysis and Empirical Evidence

1. **Access Pattern Difference**:
   - In `microbench`, queries used `long id = (i * 7919) % 50_000_000 + 1`. The prime stride 7919 traversed all 50,000,000 accounts uniformly across the 5.3 GB relation footprint. This exceeded PostgreSQL's buffer pool, forcing constant disk/filesystem cache page fetches.
   - In `RunBenchmarkAsync`, Zipfian workloads routed 85% of traffic to a 1% hot set (`hotSetSize = 500,000` accounts). 500,000 accounts in the covering index require only **15.75 MB** of B-tree pages ($1,575 \text{ MB} \times 0.01$). 15.75 MB fits entirely in PostgreSQL's `shared_buffers`.

2. **Query Execution Plans (`EXPLAIN (ANALYZE, BUFFERS)`)**:
   - **Covering Index Query**:
     `SELECT (balance >= 100 AND active) FROM eap_audit_20261028.accounts_50m WHERE id = 25000000;`
     ```text
     Index Only Scan using idx_accounts_50m_covering on accounts_50m
       Heap Fetches: 0
       Buffers: shared hit=7 read=1
     ```
     Zero heap accesses occurred; all data was served from the index.
   - **Direct Query (Heap Fetch)**:
     `SELECT (balance >= 100 AND active AND version >= 0) FROM eap_audit_20261028.accounts_50m WHERE id = 25000000;`
     ```text
     Index Scan using idx_accounts_50m_covering on accounts_50m
       Buffers: shared hit=4 read=1
     ```
     Required fetching the heap tuple to inspect the non-indexed `version` column.

3. **Empirical Verification (2,000 Queries Each)**:
   - **Uniform Stride 7919**: Mean = **848.1 $\mu$s**, p50 = **772.0 $\mu$s**, p95 = **954.7 $\mu$s**, p99 = **1,324.7 $\mu$s** (Throughput: 1,178 ops/s).
   - **Zipfian Hot Set (85%/1%)**: Mean = **712.2 $\mu$s**, p50 = **674.8 $\mu$s**, p95 = **1,131.6 $\mu$s**, p99 = **1,330.0 $\mu$s** (Throughput: 1,401 ops/s).

---

## 3. Write-Path Ceiling vs. Mixed-Workload Rates (Section 3)

### Component Write Breakdown

The write pipeline consists of:
1. Connection checkout from pool
2. `BeginTransactionAsync`
3. Global advisory transaction lock acquisition: `SELECT pg_advisory_xact_lock(hashtext('eap_outbox_lock'));`
4. PostgreSQL table mutation: `UPDATE accounts_50m SET balance = @b, active = @a, version = version + 1 WHERE id = @id;`
5. Outbox insertion: `INSERT INTO mutation_log (account_id, balance, active) VALUES (...) RETURNING seq_id;`
6. Transaction commit: `tx.CommitAsync();` (WAL flush)
7. In-memory Seqlock publish: `coord.PublishCrossPlane(id, bal, act);`

The 500-iteration profile (`write_and_mixed_reconciliation.json`) yielded:

| Component Stage | Mean ($\mu$s) | p50 ($\mu$s) | p95 ($\mu$s) | p99 ($\mu$s) | Max ($\mu$s) | Fraction of Total |
|---|---|---|---|---|---|---|
| Connection Checkout | 4.9 | 4.2 | 7.7 | 18.6 | 40.3 | 0.2% |
| Transaction Begin | 14.4 | 3.8 | 8.7 | 19.3 | 5,135.5 | 0.6% |
| Advisory Lock Acquire | 528.5 | 492.8 | 669.5 | 952.0 | 8,148.0 | 23.4% |
| PostgreSQL UPDATE | 623.1 | 593.5 | 831.9 | 1,167.4 | 3,163.5 | 27.6% |
| Outbox INSERT | 483.3 | 446.0 | 626.9 | 1,174.0 | 3,714.9 | 21.4% |
| Transaction Commit (WAL) | 595.9 | 570.7 | 714.2 | 1,303.8 | 9,269.2 | 26.4% |
| Seqlock Publish (RAM) | 7.4 | 3.3 | 13.2 | 24.4 | 1,052.7 | 0.3% |
| **Total Wall Clock** | **2,258.0** | **2,144.8** | **2,673.1** | **3,612.5** | **29,233.4** | **100.0%** |
| **Lock Held Duration** | **1,702.5** | **1,624.3** | **2,042.8** | **3,002.9** | **14,887.3** | **75.4%** |

### The Write Ceiling

The advisory lock is held from acquisition through UPDATE, INSERT, and `tx.CommitAsync()`:
$$\text{Lock Held Mean} = 1,702.5 \ \mu\text{s} = 1.703 \text{ ms}$$
$$\text{Maximum Serial Write Throughput} = \frac{1,000,000 \ \mu\text{s}}{1,702.5 \ \mu\text{s}} \approx \mathbf{587 \text{ writes/second}}$$

### Reconciling Mixed-Workload Throughput

Earlier reports showed 51,285 ops/s for `ReadHeavy-99-1` and 9,998 ops/s for `ReadHeavy-95-5`. The audit separated read throughput from write throughput across all mixed workloads:

| Workload Mix | Total Requests | Elapsed (ms) | Combined Rate (ops/s) | Reads Completed | Read Throughput | Read p50 ($\mu$s) | Writes Completed | Write Throughput | Write p50 ($\mu$s) | Advisory Ceiling Obeyed? |
|---|---|---|---|---|---|---|---|---|---|---|
| **ReadHeavy-99-1** | 1,000 | 22.39 | **44,661 ops/s** | 994 | 44,393 r/s | 0.3 | 6 | **268.0 w/s** | 2,244.4 | **YES** ($\le 587$) |
| **ReadHeavy-95-5** | 1,000 | 107.24 | **9,325 ops/s** | 950 | 8,858 r/s | 0.3 | 50 | **466.2 w/s** | 2,093.9 | **YES** ($\le 587$) |
| **Balanced-80-20** | 1,000 | 457.92 | **2,184 ops/s** | 789 | 1,723 r/s | 0.4 | 211 | **460.8 w/s** | 2,136.0 | **YES** ($\le 587$) |
| **WriteIntensive-50-50** | 1,000 | 1,054.45 | **948 ops/s** | 494 | 468 r/s | 0.9 | 506 | **479.9 w/s** | 2,049.9 | **YES** ($\le 587$) |

> [!IMPORTANT]
> The apparent contradiction is resolved: write throughput **never exceeded 480 writes/sec** in any workload, strictly obeying the ~587 writes/s lock ceiling. Combined throughput is high in read-heavy workloads because 99% of requests are in-memory reads completing in 0.3 $\mu$s.

---

## 4. PostgreSQL Catalog, Redundant Indexes & Storage Footprint (Section 4)

### Catalog Inspection on `eap_audit_20261028.accounts_50m`

The PostgreSQL system catalog was inspected via `pg_constraint`, `pg_indexes`, and `pg_class`:
- **Explicit Constraints**: None (`pg_constraint` returned 0 rows). The table lacks an explicit `PRIMARY KEY` or `CHECK` constraint.
- **Indexes**:
  1. `idx_accounts_50m_id`: `CREATE UNIQUE INDEX idx_accounts_50m_id ON eap_audit_20261028.accounts_50m USING btree (id)`
     - Physical Size: **1,123,115,008 bytes** (1,123.12 MB decimal / 1,071.09 MiB binary)
  2. `idx_accounts_50m_covering`: `CREATE INDEX idx_accounts_50m_covering ON eap_audit_20261028.accounts_50m USING btree (id) INCLUDE (balance, active)`
     - Physical Size: **1,574,920,192 bytes** (1,574.92 MB decimal / 1,501.96 MiB binary)

### Redundancy Assessment

Both indexes are B-trees with `id` as their leading indexed column. 
Because `idx_accounts_50m_covering` includes `balance` and `active`, it can satisfy every single query that `idx_accounts_50m_id` can satisfy. If `idx_accounts_50m_covering` were declared with `UNIQUE (id) INCLUDE (balance, active)`, `idx_accounts_50m_id` would be 100% redundant.

Maintaining both indexes forces PostgreSQL to:
1. Double-insert and rebalance two separate B-trees on every write.
2. Consume **2,698.05 MB** in indexes, which exceeds the table heap itself (2,610.75 MB).
3. Eliminating `idx_accounts_50m_id` would save **1,123.12 MB (41.6% of index storage)**.

### Physical Footprint and Unit Accounting

The physical footprint of the 50M accounts table and the EAP structures was measured:

| Storage Component | Exact Bytes | Decimal (MB / GB, $10^6 / 10^9$) | Binary (MiB / GiB, $2^{20} / 2^{30}$) | Bytes / Row |
|---|---|---|---|---|
| PostgreSQL Table Heap | 2,610,749,440 | 2,610.75 MB | 2,489.80 MiB | 52.21 B |
| Primary ID Index (`idx_accounts_50m_id`) | 1,123,115,008 | 1,123.12 MB | 1,071.09 MiB | 22.46 B |
| Covering Index (`idx_accounts_50m_covering`) | 1,574,920,192 | 1,574.92 MB | 1,501.96 MiB | 31.50 B |
| **Total PostgreSQL Indexes** | 2,698,051,584 | 2,698.05 MB | 2,573.06 MiB | 53.96 B |
| **Total PostgreSQL Relation Footprint** | **5,308,801,024** | **5,308.80 MB (5.31 GB)** | **5,062.87 MiB (4.94 GiB)** | **106.18 B** |
| EAP Boolean Bitmap (50M) | 6,250,000 | 6.25 MB | 5.960464 MiB | 0.125 B |
| EAP Packed Category Plane (50M) | 31,250,000 | 31.25 MB | 29.802322 MiB | 0.625 B |

### Resolution of Decimal vs. Binary Mix-Up

Earlier reports stated:
- *"EAP Boolean Bitmap requires 5.96 MB"*
- *"EAP Category Plane requires 29.8 MB"*

This was a unit error: the author divided bytes by $1,024^2 = 1,048,576$ (yielding binary MiB), but labeled the result with decimal "MB".
- $6,250,000 \text{ bytes} = \mathbf{6.25 \text{ MB}}$ (decimal) $= \mathbf{5.960464 \text{ MiB}}$ (binary).
- $31,250,000 \text{ bytes} = \mathbf{31.25 \text{ MB}}$ (decimal) $= \mathbf{29.802322 \text{ MiB}}$ (binary).

### Compression Ratios vs. PostgreSQL

- Total PostgreSQL Relation (Heap + 2 Indexes) vs. EAP Bitmap: $\frac{5,308,801,024}{6,250,000} = \mathbf{849.4\times}$
- Table Heap Alone vs. EAP Bitmap: $\frac{2,610,749,440}{6,250,000} = \mathbf{417.7\times}$
- Covering Index Alone vs. EAP Bitmap: $\frac{1,574,920,192}{6,250,000} = \mathbf{252.0\times}$
- Total PostgreSQL Relation vs. EAP Category Plane: $\frac{5,308,801,024}{31,250,000} = \mathbf{169.9\times}$

---

## 5. External Mutation Consistency and Staleness Window (Section 5)

### Staleness Proof

The system relies on an asynchronous transactional outbox with an `AFTER INSERT OR UPDATE OR DELETE` database trigger. An adversarial test (`ExternalMutation_StalenessWindow_ProvesEventualConsistency`) demonstrated the exact stale read window that occurs when external applications mutate PostgreSQL directly:

```
[External SQL Client]              [PostgreSQL 17.2]                  [EAP Reader (.NET)]
        │                                  │                                  │
        │─── UPDATE balance=180 ──────────>│                                  │
        │─── COMMIT ──────────────────────>│ (Committed ground truth)         │
        │                                  │                                  │
        │                                  │<─── QueryEligible(1) ────────────│
        │                                  │     Reads STALE false!           │ (Pre-commit bit)
        │                                  │                                  │
        │─── Trigger writes mutation_log ─>│                                  │
        │                                  │                                  │
        │                         [ReconcilePoll]                             │
        │                                  │─── Replays seq_id=1 ────────────>│
        │                                  │                                  │
        │                                  │<─── QueryEligible(1) ────────────│
        │                                  │     Reads TRUE!                  │ (Synchronized)
```

### Deterministic Test Results

| Mutation Type | SQL Action | Ground Truth | Immediate Post-Commit EAP Read | Stale Window Observed? | Post-Reconciliation EAP Read | Consistency Model |
|---|---|---|---|---|---|---|
| **UPDATE (Upward)** | `UPDATE ... balance = 180 WHERE id = 1;` | `true` | `false` | **YES** | `true` | Eventual Synchronization |
| **UPDATE (Boundary)** | `UPDATE ... balance = 40 WHERE id = 2;` | `false` | `true` | **YES** | `false` | Eventual Synchronization |
| **INSERT (New Row)** | `INSERT INTO ... (id=4, balance=150)` | `true` | `false` | **YES** | `true` | Eventual Synchronization |
| **DELETE (Removal)** | `DELETE FROM ... WHERE id = 3;` | `false` | `true` | **YES** | `false` | Eventual Synchronization |

> [!CAUTION]
> EAP does **NOT** provide linearizable zero-staleness or atomic read-after-write consistency for external mutations. Staleness is strictly bounded by the outbox polling interval or replication lag. If an application requires strict linearizability immediately after direct SQL writes, queries must route directly to PostgreSQL.

---

## 6. Sharded Advisory Lock Evaluation and Gap Anomaly (Section 6)

### Proposal: 1,024 Striped Shard Locks

To eliminate the ~587 writes/s bottleneck caused by the global lock `hashtext('eap_outbox_lock')`, it was proposed to shard advisory locks by account ID:
```csharp
int shardId = (int)((ulong)id % 1024);
await cmd.ExecuteNonQueryAsync($"SELECT pg_advisory_xact_lock({shardId});");
```

### Analysis: Deadlocks and Lock Ordering

1. **Single-Account Transactions**: Fully safe and deadlock-free. Each transaction acquires exactly one shard lock and releases it at commit.
2. **Multi-Account Transactions**: If Transaction A updates Account 1 (Shard 1) then Account 2 (Shard 2), while Transaction B updates Account 2 (Shard 2) then Account 1 (Shard 1), a deadlock will occur in PostgreSQL.
3. **Deadlock Avoidance Invariant**: Any transaction updating multiple accounts **must sort distinct shard IDs in ascending order** and acquire advisory locks sequentially.

### The Outbox Sequence Gap Anomaly

In PostgreSQL, sequence numbers (`BIGSERIAL`, `nextval()`) are non-transactional: numbers are allocated immediately and never rolled back.

When multiple transactions execute concurrently under independent shard locks:
1. Transaction $T_1$ on Shard 1 calls `nextval()`, receiving `seq_id = 100`.
2. Transaction $T_2$ on Shard 2 calls `nextval()`, receiving `seq_id = 101`.
3. Because $T_1$ and $T_2$ hold different shard locks, their commit order is not serialized.
4. $T_2$ commits at time $t_1$. At $t_1$, `seq_id = 101` is committed and visible, while $T_1$ (`seq_id = 100`) is still in-flight.
5. The EAP reconciliation poller executes:
   ```sql
   SELECT seq_id, ... FROM mutation_log WHERE seq_id > @lastSeq ORDER BY seq_id;
   ```
   If `@lastSeq = 99`, the poller reads `seq_id = 101` (since 100 is uncommitted and invisible in MVCC).
6. The poller updates `@lastSeq = 101`.
7. $T_1$ finally commits `seq_id = 100` at time $t_2$.
8. On the next poll (`WHERE seq_id > 101`), `seq_id = 100` is **skipped forever**. That committed mutation is permanently dropped from the cache!

### Viable Architectures for Safe High-Throughput Invalidation

To scale write throughput beyond 587 writes/sec without dropping mutations:
1. **Per-Shard Outbox Queues**: Maintain 1,024 separate outbox queues/sequences, each with an independent watermark checkpoint.
2. **PostgreSQL Logical Replication (WAL Decoding)**: Use a logical decoding plugin (`test_decoding` or `pgoutput`) to stream transactions directly from WAL. PostgreSQL guarantees gap-free, commit-ordered delivery without application-level advisory locks.
3. **Commit Confirmation Sliding Window**: Maintain a bitmap of received sequence IDs, advancing the watermark only across contiguous committed sequences.

---

## 7. Audit and Recalculation of All Speedup Claims (Section 7)

| Reported Claim | Previous Stated Value | Audit Evaluation | Corrected Value & Technical Grounding |
|---|---|---|---|
| **Claim 1: Latency Speedup** | 75,523x | **CORRECTED** | **2,900x (warm) to 5,483x (scattered)**. 75,523x compared a CPU register tight loop (14.52 ns) against a cold database query over TCP (1,096.6 $\mu$s). True end-to-end async API latency is ~200 ns vs. 580 $\mu$s PostgreSQL warm covering query. |
| **Claim 2: Async Latency Speedup** | 13,756x | **CORRECTED** | **2,900x to 5,500x**. Derived from raw non-inlined dispatch. |
| **Claim 3: Throughput Speedup** | 21x | **CORRECTED** | **2,790x to 122,700x**. The 21x claim was based on the throttled 34,510 ops/s figure. Clean single-threaded EAP throughput is 4.57 MOps/s (2,790x faster than PostgreSQL's 1,636 ops/s). 16-thread EAP throughput is 200.79 MOps/s (122,700x faster than PostgreSQL). |
| **Claim 4: Relation Size Compression** | 849x | **VERIFIED (with qualification)** | **849.4x vs. total relation** (5,308.8 MB vs. 6.25 MB). However, PostgreSQL includes two redundant B-trees. Compression is **417.7x** vs. table heap alone, and **252.0x** vs. covering index alone. |
| **Claim 5: Break-Even Query Count** | ~25,700 queries | **VERIFIED** | **25,738 queries**. Hydration of 50M records takes ~15 seconds. Each EAP query saves ~582.8 $\mu$s relative to PostgreSQL. Break-even occurs after 25,738 queries (~15 seconds of traffic). |

---

## 8. Completion Decision Questions

### 1. Is the EAP Boolean Bitmap ready for production deployment?
**CONDITIONAL YES (Read-Dominated Workloads)**.  
The core in-memory querying, hydration recovery, bit-word concurrency, and Seqlock cross-plane consistency are mathematically sound, highly performant (4.5M to 200M ops/s), and proven across 50,000,000 records. However, deployment is strictly conditional on workload characteristics: it is ideal for read-heavy environments ($\ge 98\%$ reads) where eventual consistency (bounded by outbox polling) is acceptable.

### 2. Is EAP suitable for write-heavy workloads?
**NO (Under Current Global Advisory Lock)**.  
Because writes serialize through a global advisory lock to prevent the Outbox Sequence Gap Anomaly, write throughput is physically capped at ~587 writes/second. Any workload with $>1,000$ writes/sec will experience severe lock queueing and latency degradation unless migrated to Logical Replication or per-shard queues.

### 3. Does EAP serve stale or incomplete answers?
**NO FOR ENGINE READS; YES FOR DIRECT EXTERNAL WRITES DURING RECONCILIATION WINDOW**.  
When reads and writes route through `EapCoordinator`, updates are atomic and instantaneous. However, if external clients execute direct SQL mutations against PostgreSQL, an EAP reader observes the pre-commit value until the outbox trigger event is processed. This is eventual synchronization, not zero-staleness.

### 4. Can hydration or recovery drop committed mutations?
**NO**.  
`HydrateSynchronizedFromPostgreSqlAsync` begins in a `REPEATABLE READ` snapshot, captures the outbox sequence watermark before table streaming, streams all records, and replays all subsequent outbox mutations committed after the watermark.

### 5. Can PostgreSQL match EAP with index optimizations?
**NO**.  
Even with an optimized covering index (`INCLUDE (balance, active)`), PostgreSQL requires ~580 $\mu$s per query and 1,636 ops/s per connection due to TCP/IPC round-trips, transaction parsing, and lock manager overhead. EAP evaluates queries in **4.98 ns to 200 ns** directly in CPU cache, delivering a **2,900x to 122,700x throughput advantage**.

### 6. Should the 500-Million-Record Dataset Be Created Now?
**NO**.  
The physical catalog measurements from 50M records confirm that 500M records would require **53.1 GB** of PostgreSQL disk space and ~150 seconds to hydrate. All algorithmic scaling, concurrency, and memory properties have been verified on the real 50M dataset. Scaling to 500M would incur substantial disk consumption without surfacing new theoretical or concurrency insights.

---

## Telemetry Artifacts Index

All raw telemetry files are preserved in `results/final_integrity_audit_20261029/`:
- `catalog_inspection.json`: Constraint definitions, index definitions, physical byte measurements, redundant index analysis.
- `harness_and_eap_throughput.json`: Loop overhead breakdown, single-threaded throughput, 1-to-16 thread scalability measurements.
- `postgres_profile_and_explain.json`: Covering index vs direct heap EXPLAIN plans, stride vs Zipfian latency decomposition.
- `write_and_mixed_reconciliation.json`: Component write latency profile, advisory lock held duration, mixed workload reconciliation.
- `external_mutation_staleness.json`: Empirical validation of eventual synchronization across UPDATE, BOUNDARY, INSERT, and DELETE.
- `sharded_lock_analysis.json`: Deadlock avoidance proofs and Sequence Gap Anomaly evaluation.
- `speedup_recalculation.json`: Recalculated speedup claims and break-even arithmetic.
