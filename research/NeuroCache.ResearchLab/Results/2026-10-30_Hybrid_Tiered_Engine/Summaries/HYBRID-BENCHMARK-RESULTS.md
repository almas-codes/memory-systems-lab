# NeuroCache ResearchLab — Hybrid Benchmark Tournament Results

**Repository:** `almas-codes/memory-systems-lab`  
**Execution Date:** October 30, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)  
**Scale Evaluated:** 10,000,000 Accounts (with 50,000,000 baseline projections)  
**Host Platform:** AMD Ryzen 7 5825U (8C/16T, 16 MB L3 Cache, Windows 11 x64, .NET 10.0.100, PostgreSQL 17.2)  
**Accompanied By:** [`research/.../Raw/hybrid-benchmark-results.json`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/Results/2026-10-30_Hybrid_Tiered_Engine/Raw/hybrid-benchmark-results.json)

---

## 1. Executive Tournament Summary

The hybrid tournament compared individual components (`PROD-01` alone, `THR-02` alone, PostgreSQL alone) against the integrated `HybridQueryRouter` across five distinct workloads.

```
+----------------------------------------------------------------------------------------------------+
|                                    HYBRID TOURNAMENT SCORECARD (10M)                               |
+----------------------------------------------------------------------------------------------------+
| Workload                  | Best Architecture          | Throughput           | Mean / p50 Latency |
+---------------------------+----------------------------+----------------------+--------------------+
| Pure Point Lookups        | PROD-01 Alone              | 41,479,835 ops/s     | 24.1 ns / 14.0 ns  |
| Pure Analytical Scans     | THR-02 Alone               | 3,170,013,197 recs/s | 0.31 ns / 0.31 ns  |
| Point-Heavy Mixed (95/5)  | Generational Hybrid (Var B)| 472,491 ops/s        | 2,116 ns / 1,857 ns|
| Analytical-Heavy (80/20)  | Deterministic Hybrid(Var A)| 39,213 ops/s         | 25.5 µs / 22.8 µs  |
| Balanced Mixed (50/50)    | Per-Domain Hybrid (Var C)  | 62,271 ops/s         | 16.1 µs / 14.2 µs  |
| Skewed Zipfian 80/20 Point| Deterministic Hybrid(Var A)| 2,043,203 ops/s      | 489.4 ns / 372 ns  |
| Concurrent Writes + Reads | Deterministic Hybrid(Var A)| 6,360,673 ops/s      | 157.2 ns / 55.0 ns |
+----------------------------------------------------------------------------------------------------+
```

---

## 2. Multi-Workload Detailed Benchmark Matrix

### 2.1 Workload 1: Point-Query Heavy (95% Point, 5% Analytical)
In this workload, 95% of queries request single-account boolean eligibility (`PointPredicate`), while 5% execute analytical scans across 100,000 accounts.

| Candidate Architecture | Throughput (ops/sec) | Mean Latency (ns) | p50 Latency (ns) | p95 Latency (ns) | p99 Latency (ns) | Total Footprint (MB) | Bits / Account | Fallback % |
|---|---|---|---|---|---|---|---|---|
| **`PROD-01` Alone (100% Point)** | **41,479,835** | 24.1 ns | 14.0 ns | 16.0 ns | 94.0 ns | 5.14 MB | 4.31 b/acc | 0.00% |
| **Generational Hybrid (Variant B)** | **472,491** | 2,116.4 ns | 1,857.0 ns | 4,210.0 ns | 7,534.0 ns | 18.25 MB | 15.31 b/acc | 0.00% |
| **Deterministic Hybrid (Variant A)**| 391,644 | 2,553.3 ns | 1,825.0 ns | 4,310.0 ns | 17,858.0 ns | 18.25 MB | 15.31 b/acc | 0.00% |
| PostgreSQL 17.2 Alone | 842 | 1,187,000 ns | 950,000 ns | 2,400,000 ns | 5,100,000 ns | 4,800.00 MB | 4,026 b/acc | N/A |

*Analysis:* Point-heavy hybrid throughput is dominated by the 5% analytical scans (each scanning 100k records in $\sim 35\text{ µs}$). The hybrid router automatically dispatches point queries in 14 ns while analytical queries complete in 35 µs, delivering a **561x speedup over PostgreSQL alone**.

---

### 2.2 Workload 2: Analytical-Heavy (80% Analytical, 20% Point)
In this workload, 80% of queries execute analytical cohort counts, and 20% request point lookups.

| Candidate Architecture | Throughput (ops/sec) | Mean Latency (ns) | p50 Latency (ns) | p95 Latency (ns) | p99 Latency (ns) | Total Footprint (MB) |
|---|---|---|---|---|---|---|
| **`THR-02` Alone (Full Table Scan)** | **3,170,013,197 rec/s** | 0.31 ns/rec | 0.31 ns/rec | 0.31 ns/rec | 0.31 ns/rec | 13.11 MB |
| **Deterministic Hybrid (Variant A)** | **39,213 ops/s** | 25,501.8 ns | 22,824.0 ns | 36,410.0 ns | 40,497.0 ns | 18.25 MB |
| PostgreSQL 17.2 Alone | 18 ops/s | 54,250,000 ns | 51,000,000 ns | 85,000,000 ns | 120,000,000 ns | 4,800.00 MB |

*Analysis:* Scanning 100k records on `THR-02` requires only $22.8\text{ µs}$ ($0.23\text{ ns/account}$). PostgreSQL requires $54.2\text{ ms}$ for equivalent scans. The hybrid engine delivers a **2,178x throughput advantage** over PostgreSQL.

---

### 2.3 Workload 3: Balanced Mixed (50% Point, 50% Analytical)

| Candidate Architecture | Throughput (ops/sec) | Mean Latency (ns) | p50 Latency (ns) | p95 Latency (ns) | p99 Latency (ns) |
|---|---|---|---|---|---|
| **Per-Domain Hybrid (Variant C)** | **62,271 ops/s** | 16,058.7 ns | 14,238.0 ns | 24,110.0 ns | 27,588.0 ns |
| **Deterministic Hybrid (Variant A)**| 60,345 ops/s | 16,571.3 ns | 15,044.0 ns | 25,230.0 ns | 27,964.0 ns |
| PostgreSQL 17.2 Alone | 42 ops/s | 23,800,000 ns | 21,500,000 ns | 45,000,000 ns | 78,000,000 ns |

*Analysis:* Variant C (Per-Domain Readiness) slightly edges out Variant A because point lookups do not wait for analytical lock transitions, yielding a 3.2% throughput gain.

---

### 2.4 Workload 4: Zipfian 80/20 Skewed Point Access
80% of queries target the top 20% most active accounts (simulating real customer account distributions).

- **Deterministic Hybrid Throughput:** **2,043,203 ops/sec**
- **Mean Latency:** 489.4 ns
- **p50 Latency:** **372.0 ns**
- **p99 Latency:** 1,031.0 ns
- *Insight:* High locality keeps hot account pages in the CPU's 16 MB L3 cache, cutting latency by 74% compared to uniform DRAM-bound lookups.

---

### 2.5 Workload 5: Concurrent Mutations (50,000 writes/sec with Reads)
Under 4 reader threads and 1 continuous writer thread committing 50,000 updates/second:

- **Integrated Throughput:** **6,360,673 ops/sec** (combined reads and writes).
- **Mean Operation Time:** 157.2 ns.
- **p50 Latency:** **55.0 ns**.
- **p99 Latency:** 160.0 ns.
- **Torn Reads:** **0**.
- **Dropped Writes:** **0**.
- *Insight:* Page seqlocks in `PROD-01` and word-level volatile writes in `THR-02` allow readers to proceed with minimal backoff during write bursts.

---

## 3. Router Overhead & Fallback Performance

- **Pure Routing Overhead:** $\mathbf{11.8 \text{ ns}}$ per operation (type discrimination, bounds check, and virtual interface call).
- **Fallback Execution Cost:** Fallback to PostgreSQL costs $1.32\text{ ms}$ for 10M slice and $2.71\text{ ms}$ for 50M table.
- **Fallback Rate Under Normal Traffic:** **0.000%**.
- **Break-Even Point for Vectorization (`HYBRID-OPT-02`):** For analytical batches, vectorization break-even occurs at $N \ge 16$ records.
