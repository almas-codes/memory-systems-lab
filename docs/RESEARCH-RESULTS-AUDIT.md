# NeuroCache ResearchLab: Independent Systems & Measurement Audit
## Rigorous Verification of Experimental Results, Raw Telemetry, Code Implementations, and Dataset Manifests

**Audit Date**: October 28, 2026  
**Auditor**: Principal Performance & Systems Auditor  
**Repository Target**: `NeuroCache.ResearchLab`  
**Host Hardware**: AMD Ryzen 7 5825U (8 Cores, 16 Threads), 16.0 GB DDR4, INTEL SSDPEKNW512GZL NVMe SSD  
**Host Runtime**: .NET 10.0.12 LTS / C# 13, Microsoft Windows 11 Home x64  
**Database**: PostgreSQL 17.2 x64 (Localhost:5432, Database: `neurocache_eap_lab`, Tablespace: `d_tablespace`)  

---

## 1. Executive Summary & Purpose of Audit

This audit was conducted to independently verify the claims, mathematical derivations, source code implementations, and raw benchmark measurements reported in `docs/FINAL-REPORT.md`, `docs/EAP-500M-REPORT.md`, and `docs/COMPREHENSIVE-RESEARCH-REPORT.md`.

### Core Verdict
The core algorithmic findings—specifically that **pure CPU recomputation dominates NVMe storage reads (~4,700x)**, that **dense bitwise planes require orders of magnitude less memory (0.125 bytes/item)**, and that **single-seek capsules outperform two-phase index reads (7.6x)**—are **physically valid and empirically verified** by raw benchmark telemetry.

However, the audit identified **critical arithmetic errors, unit confusions, dataset scale discrepancies, and conflations of theoretical extrapolations with measured numbers**:
1. **The Claimed 500-Million-Account Experiment was Executed on 1,000,000 Records**: The PostgreSQL table `eap_lab.accounts_500m` contains exactly **1,000,000 rows** (50 MB table + 52 MB index = 101 MB total). The raw benchmark output (`results/eap-500m/eap_benchmark_results.json`) explicitly records `"DatasetRowCount": 1000000`. The 500M scale was a theoretical projection ($500M \times 1 \text{ bit} = 59.6 \text{ MiB}$), not an empirical 500-million-row database execution.
2. **Disk Footprint Arithmetic Error (Mixing Decimal GB and Binary GiB)**: The 100M-record dataset on disk occupies **11.20 GB (decimal)** or **10.43 GiB (binary)** (8.80 GB extent segments + 2.40 GB dense index). The previous report took **10.43** (which was the total dataset in binary GiB), treated it as decimal GB for extents alone, and added 2.40 GB index, reporting an erroneous **12.83 GB**. Furthermore, capsule storage on disk is **27.79 MB (26.5 MiB)**, holding ~463,185 benchmarked capsules, NOT 6.00 GB.
3. **The "33,000,000 ops/s" Claim Lacks Raw Evidence**: The claim that EAP achieves 33,000,000 ops/s at 30 ns p50 was an unverified theoretical calculation based on CPU register bit-shifting, not a benchmark output. The actual measured throughput of EAP in the raw benchmark file was **18,385.9 ops/s** under a 95/5 Zipfian read/write mix.
4. **`ConcurrentDictionary` Memory Did Not Measure 8.8 GB**: The benchmark implementation of `DotNetDictionaryStrategy` explicitly enforced a memory cap (`_residentBytes + estSize <= _budgetLimit`) and stopped caching once the budget was reached. The test only cached a small subset of the keys. The claimed 8.8 GB was a theoretical estimate ($100M \times 88 \text{ B}$), never resident in RAM during testing.
5. **Memory-Mapped PageCache Did Not Enforce RAM Budgets**: `MemoryMappedStoreStrategy` completely ignored the `--ram-budget` parameter and relied on the OS kernel file cache. Process working set (`WorkingSet64`) reported only 100–280 MB because OS standby cache pages were not accounted for. Comparing it against strictly budgeted strategies under "Budget-128MB" was physically invalid.
6. **EAP Mutation Crash-Consistency Vulnerability**: In `EapCoordinator`, `CommitAsync()` to PostgreSQL occurs before `SetEligible()` updates the in-memory bitmap. A crash between database commit and bitmap update causes persistent memory-database desynchronization without a recovery WAL.

---

## 2. Table of Major Claims: Verification Status

| Claim / Metric | Reported Value | Actual Measured / Code Reality | Status | Evidence Path |
|---|---|---|---|---|
| **PostgreSQL Dataset Row Count** | 500,000,000 rows | **1,000,000 rows** (`accounts_500m`) | **FAILED** | PostgreSQL query `count(*)`: 1,000,000; `data/eap_manifest.json` |
| **PostgreSQL Database Storage Size** | ~58.5 GB | **101 MB** (50 MB table + 52 MB index) | **FAILED** | `pg_total_relation_size`: 106,242,048 bytes |
| **100M Dataset On-Disk Footprint** | 12.83 GB | **11.20 GB (10.43 GiB)** | **CORRECTED** | 20 segments (8.80 GB) + index (2.40 GB). Unit confusion (GiB vs GB). |
| **Capsule File On-Disk Size** | 6.00 GB | **27.79 MB (26.5 MiB)** | **CORRECTED** | `data/capsules/projection_capsules.dat` (27,791,100 bytes; 463,185 capsules) |
| **Pure Recompute vs NVMe SSD** | 12.4 ns vs 58,600 ns | **12.4 ns vs 58,600 ns (p50)** | **VERIFIED** | `results/micro_suite.json`: `Micro-PureRecomputeVsSSD` |
| **Single-Read Capsule Speedup** | 15,896 vs 2,086 ops/s | **15,896 vs 2,086 ops/s (7.6x)** | **VERIFIED** | `results/micro_suite.json`: `Micro-SingleReadCapsule` |
| **EAP In-Memory Query Latency** | 33,000,000 ops/s (30 ns) | **18,385.9 ops/s (100 ns p50)** (95/5 mix) | **UNVERIFIED** | 33M was a CPU formula; raw test has 18,385.9 ops/s (`results/eap-500m/`) |
| **EAP 500M RAM Residency** | 59.60 MiB (62.5 MB) | **Theoretical for 500M; 122 KiB actual** | **CORRECTED** | Actual for 1M records was 125,000 bytes (122 KiB); 59.6 MiB is valid math for 500M. |
| **EAP Category Plane Initialization** | Hydrated from DB | **Empty initialized (Zeroes)** | **FAILED** | `Candidate6_EapPackedCategory.InitializeAsync` never read from DB |
| **Concurrent Bit Updates (Same Word)** | Update-safe | **Update-safe (Interlocked.Or/And)** | **VERIFIED** | Verified by newly executed deterministic concurrency test |
| **Concurrent Nibbles (Same Byte)** | Update-safe | **Update-safe (lock (_categories))** | **VERIFIED** | Verified by newly executed deterministic concurrency test |
| **EAP Crash-Consistency Protocol** | Fully synchronized | **Vulnerable to desync on crash** | **FAILED** | DB commits before memory bit flips; no WAL replay |
| **Capsule Physical Read Size** | 60-byte device read | **4,096-byte hardware read + OS buffer** | **CORRECTED** | Win32 ReadFile / OS page cache / NVMe 4KB sector; 2 reads if payload > 0 |
| **ConcurrentDictionary 100M RAM** | 8.8 GB consumed | **Bounded by budget (59–116 MB)** | **CORRECTED** | 8.8 GB was theoretical ($100M \times 88\text{B}$). Strategy stops caching at budget. |
| **MemoryMapped RAM Budget** | Enforced 128MB–1024MB | **Unconstrained OS Page Cache** | **FAILED** | RAM budget ignored by MMap strategy; only private WS measured |
| **Shared State Witness 50% Churn** | 80,249 ops/s | **80,249 ops/s** | **VERIFIED** | `results/adversarial_suite.json`: `Adversarial-HighChurn50Percent` |

---

## 3. Detailed Audit Findings

### 3.1 Check 2 & 3: Database Verification (`eap_lab.accounts_500m`)

An independent read-only inspection was executed on the live PostgreSQL server:
- **Server Version**: PostgreSQL 17.2 x64 on `localhost:5432`
- **Database**: `neurocache_eap_lab`
- **Schema**: `eap_lab`
- **Actual Table Name**: `accounts_500m` (not `accounts`)
- **Table Mode**: `UNLOGGED` (WAL logging disabled during benchmark ingestion)

#### Actual Schema:
```sql
Table "eap_lab.accounts_500m" (UNLOGGED)
 Column  |  Type   | Nullable
---------+---------+----------
 id      | bigint  | not null
 balance | integer | not null
 active  | boolean | not null
 version | bigint  | not null
Indexes:
    "idx_accounts_500m_id" UNIQUE, btree (id)
    "idx_accounts_500m_covering" btree (id) INCLUDE (balance, active)
```

#### Actual Row Counts & Storage Sizes:
```sql
SELECT count(*) AS total_rows, min(id) AS min_id, max(id) AS max_id FROM eap_lab.accounts_500m;
-- Result:
-- total_rows = 1,000,000
-- min_id     = 1
-- max_id     = 1,000,000

SELECT pg_size_pretty(pg_relation_size('eap_lab.accounts_500m')) AS table_size,
       pg_size_pretty(pg_indexes_size('eap_lab.accounts_500m'))  AS index_size,
       pg_size_pretty(pg_total_relation_size('eap_lab.accounts_500m')) AS total_size;
-- Result:
-- table_size = 50 MB (52,183,040 bytes)
-- index_size = 52 MB (54,059,008 bytes)
-- total_size = 101 MB (106,242,048 bytes)
```

#### Audit Finding on Row Count Discrepancy:
- The database contains **1,000,000 rows**, not 500,000,000 rows.
- The manifest file `data/eap_manifest.json` confirms:
  ```json
  "TableName": "accounts_500m",
  "RowCount": 1000000,
  "TableBytes": 52183040,
  "IndexBytes": 54059008,
  "GenerationDuration": "00:00:03.4826140"
  ```
- The benchmark file `results/eap-500m/eap_benchmark_results.json` records:
  ```json
  "CandidateName": "PostgreSQL-Direct",
  "DatasetRowCount": 1000000,
  "ThroughputOpsPerSec": 958.26
  ```
- **Conclusion**: The prior reports presented the benchmark as having been physically executed against a 500-million-row database. In reality, **a 1,000,000-row pilot dataset was generated and tested**, and the memory requirements of 500M records were mathematically extrapolated.

---

### 3.2 Check 4: 100-Million-Record Disk Footprint Arithmetic

We audited the physical files on NVMe volume `D:\` (`d:\Projects\ProjectCache\NeuroCache.ResearchLab\data`):

| File Name | Exact Byte Size | Decimal (MB / GB) | Binary (MiB / GiB) | Description |
|---|---|---|---|---|
| `segment_00000.dat` .. `_00019.dat` (20 files) | 440,000,000 bytes each | 440.00 MB / segment (8.80 GB total) | 419.62 MiB / segment (8.196 GiB total) | Raw record extent segments |
| `extents.idx` | 2,400,000,000 bytes | 2,400.00 MB (2.40 GB) | 2,288.82 MiB (2.235 GiB) | Dense 24-byte index file |
| `data/manifest.json` | 364 bytes | < 0.01 MB | < 0.01 MiB | Dataset manifest |
| `data/capsules/projection_capsules.dat` | 27,791,100 bytes | 27.79 MB | 26.50 MiB | Aligned 60-byte capsules (~463,185 records) |
| **Actual Total Persisted Bytes** | **11,227,791,464 bytes** | **11.23 GB** | **10.46 GiB** | Complete storage on disk |

#### Reconciliation of Previous Report Arithmetic:
- `data/manifest.json` records: `"TotalBytesOnDisk": 11200000000` (11.20 GB).
- $11,200,000,000 \text{ bytes} \div 1024^3 = \mathbf{10.4308 \text{ GiB}}$.
- **The Error**: The previous report took **10.43** (which was the binary GiB of the *entire* 20 segments + index), incorrectly treated it as decimal GB of *only the segments*, and then added 2.40 GB for the index, reporting:
  $$10.43 \text{ GB (heap)} + 2.40 \text{ GB (index)} = 12.83 \text{ GB (erroneous)}$$
- **The Capsule Error**: The previous report claimed capsules consumed **6.00 GB** on disk ($100M \times 60 \text{ B}$). In reality, only **27.79 MB** of capsules were persisted (representing ~463,185 capsules written during microbenchmark runs). The full 100M capsule store was never generated on disk.

---

### 3.3 Check 5: EAP Hydration & Break-Even Recalculation

#### Measured Ingestion & Hydration Throughput:
- **Measured Ingestion**: 1,000,000 rows generated into PostgreSQL UNLOGGED table in **3.48 seconds** = **287,140 rows/second**.
- **Hydration Rate (Streaming Npgsql Reader)**: 1,000,000 rows streamed from PostgreSQL into `EapBooleanBitmap` in **1.85 seconds** = **540,540 rows/second**.

#### Recalculated 500-Million Record Projections:
- Full 500M row ingestion into PostgreSQL:
  $$\text{Time} = \frac{500,000,000 \text{ rows}}{287,140 \text{ rows/sec}} \approx \mathbf{1,741 \text{ seconds (29.0 minutes)}}$$
- Full 500M row hydration from PostgreSQL into bitmap:
  $$\text{Time} = \frac{500,000,000 \text{ rows}}{540,540 \text{ rows/sec}} \approx \mathbf{925 \text{ seconds (15.4 minutes)}}$$
- The previous report claimed a 500M build took 154 seconds (2.5 minutes). At measured throughput, a 500M hydration requires **~15.4 minutes**, not 2.5 minutes.

#### Recalculated Break-Even Analysis:
We must strictly distinguish between **Client Wall-Clock Latency Saved** and **Database CPU Time Saved**:

1. **Client Wall-Clock Latency Savings**:
   - PostgreSQL Direct query p50: ~550 $\mu$s (including connection checkout and wire round-trip).
   - EAP query p50: ~0.1 $\mu$s.
   - Net latency saved per query: **~549.9 $\mu$s (0.00055 s)**.
   - For the **actual 1M dataset** (Hydration = 1.85 seconds):
     $$\text{Break-Even Queries} = \frac{1.85 \text{ seconds}}{0.00055 \text{ s/query}} \approx \mathbf{3,364 \text{ queries}}$$
   - For a **hypothetical 500M dataset** (Hydration = 925 seconds):
     $$\text{Break-Even Queries} = \frac{925 \text{ seconds}}{0.00055 \text{ s/query}} \approx \mathbf{1,681,818 \text{ queries}}$$

2. **Database Engine CPU Time Saved**:
   - The PostgreSQL backend process consumes ~45 $\mu$s of CPU time per index-lookup query (the rest is network/socket waiting).
   - For a 500M dataset (925 seconds of server build CPU):
     $$\text{Break-Even Queries (Server CPU)} = \frac{925 \text{ seconds}}{0.000045 \text{ s/query}} \approx \mathbf{20,555,555 \text{ queries}}$$
- **Verdict**: The prior report's claim of "~308,000 queries" was derived from an unverified 154-second hydration figure.

---

### 3.4 Check 6: EAP Mutation Concurrency & Protocol Audit

#### Concurrency Mechanism Audit:
- `EapBooleanBitmap.SetEligible` implements:
  ```csharp
  if (eligible)
      Interlocked.Or(ref _bits[word], mask);
  else
      Interlocked.And(ref _bits[word], ~mask);
  ```
- `Interlocked.Or` and `Interlocked.And` compile to atomic x86-64 `lock or` / `lock and` instructions.
- **Empirical Verification**: We added and executed a deterministic multi-threaded test (`EapBooleanBitmap_ConcurrentUpdates_SameWord_NoUpdatesLost`). 64 concurrent threads repeatedly updated all 64 bits of Word 0 across 10,000 iterations. **Zero updates were lost; all 64 bits matched the exact expected bitmask**.
- `EapPackedCategoryPlane` uses `lock (_categories)` to serialize adjacent nibble updates sharing a single byte. Verified by `EapPackedCategoryPlane_ConcurrentNibbles_SameByte_NoUpdatesLost` with 0 corruptions.

#### Critical Synchronization & Crash Failure Mode:
In `EapCoordinator.UpdateAccountAsync`:
```csharp
await tx.CommitAsync(ct); // 1. Authoritative PostgreSQL commit

// 2. In-memory answer plane update
_bitmap.SetEligible(id, newEligible);
_categoryPlane.SetAccount(id, newBalance, newActive);
```
- **Vulnerability**: If the process crashes, experiences an unhandled exception, or loses power *after* `tx.CommitAsync()` but *before* `_bitmap.SetEligible()`, the PostgreSQL table has recorded the new state, but the in-memory bitmap reflects the old state.
- **Absence of Recovery Log**: `EapCoordinator` does not maintain a mutation WAL or persistent checkpoint. If the application crashes, consistency can only be restored by a complete re-hydration (`BuildFromPostgreSqlAsync`).
- **Initialization Flaw**: In `Candidate6_EapPackedCategory.InitializeAsync`, the category plane was instantiated with `new EapPackedCategoryPlane(maxId)` but never hydrated from PostgreSQL! It served benchmark queries against zeroed categories.

---

### 3.5 Check 7: Capsule File I/O Implementation Audit

We inspected `src/NeuroCache.Storage/CapsuleStore.cs`:
- **File Opening**:
  ```csharp
  _capsuleStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, 
                                  FileShare.ReadWrite, 64 * 1024, FileOptions.RandomAccess);
  ```
- **Read Mechanism**:
  ```csharp
  Span<byte> headerBuf = stackalloc byte[CapsuleHeader.HeaderSize]; // 60 bytes
  int read = RandomAccess.Read(_capsuleHandle, headerBuf, offset);
  ...
  if (header.ResultLength > 0)
  {
      int payloadRead = RandomAccess.Read(_capsuleHandle, destination.Slice(0, (int)header.ResultLength), 
                                          offset + CapsuleHeader.HeaderSize);
  }
  ```

#### Physical Storage Reality:
1. **Device Transfer Size**: Describing a 60-byte capsule read as a "60-byte physical device transfer" is physically incorrect. The PCIe NVMe controller operates in minimum LBA blocks of **4,096 bytes (4 KB)**. The OS kernel transfers at least one 4 KB memory page.
2. **Buffering**: `FileStream` was created with `FileOptions.RandomAccess` (which enables Windows OS caching and page cache readahead), NOT `FILE_FLAG_NO_BUFFERING`. Therefore, repeated capsule reads in benchmarks hit the Windows file cache, explaining sub-60 $\mu$s latencies.
3. **Double System Call**: If `ResultLength > 0`, `TryReadCapsule` executes **two separate Win32 `ReadFile` calls** (one for the 60-byte header, one for the payload). It only acts as a single read when `ResultLength == 0` (header-only projection).

---

### 3.6 Check 8 & 9: Workload Operations & `ConcurrentDictionary` Bounding

#### Actual Benchmark Request Counts:
The benchmark suite did NOT run 100M or 500M queries:
- `SmokeSuite`: 5,000 requests per strategy.
- `MicroSuite`: 20,000 requests per strategy.
- `FullMatrixSuite`: 25,000 requests per run (concurrency = 4).
- `BudgetSuite`: 20,000 requests per run (concurrency = 4).
- `EapBenchmarkSuite`: 10,000 requests per candidate.

#### Reconciliation of `ConcurrentDictionary` Memory:
In `DotNetDictionaryStrategy.cs`:
```csharp
int estSize = 64 + record.Payload.Length;
if (Interlocked.Read(ref _residentBytes) + estSize <= _budgetLimit)
{
    var cached = new CachedEntry { ... };
    if (_map.TryAdd(key, cached))
        Interlocked.Add(ref _residentBytes, estSize);
}
else
{
    Interlocked.Increment(ref _stats.Fallbacks); // Disk fallback
}
```
- **The Explanation**: The benchmarked `ConcurrentDictionary` never cached the entire 100M-record dataset. It cached only entries up to `_budgetLimit` (e.g. 128 MB or 256 MB) and fell back to NVMe extents for all remaining requests.
- The 8.8 GB cited in the report was a theoretical calculation ($100M \times 88 \text{ B}$), which was never present in RAM.

---

### 3.7 Check 10: Memory-Mapped PageCache Audit

In `MemoryMappedStoreStrategy.cs`:
- The strategy calls `_store.InitializeAsync(..., enableMMap: true)`.
- It **completely ignores** `options.RamBudgetBytes`.
- In `BenchmarkHarness.cs`, memory is recorded using:
  ```csharp
  proc.Refresh();
  peakWs = Math.Max(peakWs, proc.WorkingSet64);
  ```
- **Audit Finding**: `Process.WorkingSet64` measures only the private working set committed to the process address space. It does NOT count pages mapped into the Windows OS Standby Cache. When `MemoryMappedStoreStrategy` ran, Windows cached extents in available system RAM (up to 16 GB), while the benchmark recorded only 100–280 MB of private working set. Claiming that `MemoryMappedPageCache` obeyed strict 128 MB or 256 MB budgets was physically false.

---

## 4. Corrected Performance Comparison Table

Below is the audited performance comparison table containing **strictly verified empirical measurements** from raw JSON telemetry files:

| Strategy / Architecture | Actual Workload Tested | Verified Dataset Size | Verified Ops/sec | Verified p50 ($\mu$s) | Verified p95 ($\mu$s) | Verified p99 ($\mu$s) | Measured Peak Private WS | Evidence Source |
|---|---|---|---|---|---|---|---|---|
| **EAP Boolean Bitmap** | Zipf-95Read-5Write | 1,000,000 rows | **18,385.9** | **0.1** | 1.7 | 1,006.3 | 60.0 MB | `results/eap-500m/` |
| **EAP Boolean Bitmap** | Zipf-80Read-20Write | 1,000,000 rows | **5,513.1** | **0.2** | 959.7 | 1,123.9 | 60.4 MB | `results/eap-500m/` |
| **PostgreSQL Covering Index** | Zipf-95Read-5Write | 1,000,000 rows | **1,718.1** | **560.3** | 745.0 | 1,108.5 | 56.0 MB | `results/eap-500m/` |
| **PostgreSQL Direct** | Zipf-95Read-5Write | 1,000,000 rows | **958.3** | **691.7** | 874.7 | 1,252.1 | 49.3 MB | `results/eap-500m/` |
| **Pure Recompute (CPU)** | Micro-Recompute | 100,000,000 rows | **80,645,000\*** | **0.012** (12.4ns) | 0.018 | 0.024 | 59.0 MB | `results/micro_suite.json` |
| **Single-Read Capsule** | Micro-Capsule | 100,000,000 rows | **15,896.0** | **58.6** | 102.7 | 133.1 | 67.0 MB | `results/micro_suite.json` |
| **Direct Extent Read (NVMe)**| Micro-Capsule | 100,000,000 rows | **2,086.0** | **439.0** | 820.8 | 1,242.0 | 62.0 MB | `results/micro_suite.json` |
| **Sufficiency Fabric** | WorkingSet-10xRAM | 100,000,000 rows | **41,013.0** | **138.2** | 1,236.3 | 1,960.7 | 117.0 MB | `results/full_matrix_suite.json` |
| **NeuroCache Ω** | WorkingSet-10xRAM | 100,000,000 rows | **35,257.0** | **316.1** | 1,408.5 | 2,261.3 | 116.0 MB | `results/full_matrix_suite.json` |
| **Direct Baseline** | WorkingSet-10xRAM | 100,000,000 rows | **4,870.0** | **3,247.7** | 7,274.7 | 10,394.5 | 116.0 MB | `results/full_matrix_suite.json` |
| **MemoryMappedPageCache** | WorkingSet-FitInRAM | 100,000,000 rows | **2,223,448.0\*\*| **1.1** | 4.2 | 122.4 | 100.0 MB | `results/full_matrix_suite.json` |

*\* Extrapolated from per-operation register micro-timing (12.4 ns).*  
*\*\* Relies on unbudgeted OS kernel page cache; RAM budget was not enforced.*

---

## 5. Architectural Recommendations: What Remains Worth Testing

Based on the verified evidence:

1. **Keep & Advance: Exact Answer Planes (EAP)**:
   - Even on the audited 1M-record dataset, EAP delivers an **18x to 20x throughput improvement over direct database queries** under concurrent write mutations, and **~1,000x to 5,000x on pure read paths**.
   - The physical RAM scaling math (0.125 bytes per record) is verified.
   - **Required Work**: Add a crash-recovery WAL or transactional outbox to fix the mutation synchronization vulnerability.

2. **Keep & Advance: Single-Read Persistent Capsules**:
   - The 7.6x speedup over two-phase index-and-extent reads is genuine and verified by raw microbenchmarks (**58.6 $\mu$s vs 439.0 $\mu$s**).
   - **Required Work**: Implement aligned 4,096-byte direct I/O (`FILE_FLAG_NO_BUFFERING`) to prevent kernel cache pollution and measure true cold physical media latency.

3. **Keep & Advance: Sufficiency Recomputation**:
   - Recomputing outputs in CPU registers from 36-byte validity certificates avoids all disk I/O and outperforms direct reads by ~8.4x under memory pressure (**41,013 ops/s vs 4,870 ops/s**).

4. **Discard: Unbounded Managed Caches & Naive MMap Comparisons**:
   - Unbounded `ConcurrentDictionary` and `MemoryCache` should not be used as comparisons for large datasets without explicit budget bounding.
   - `MemoryMappedFile` benchmarks must not be compared against strict RAM budgets unless operating within an enforced cgroup or Windows Job Object with capped memory working sets.
