# NeuroCache ResearchLab — Tournament Benchmark Results

**Repository:** `almas-codes/memory-systems-lab`  
**Date:** October 29, 2026  
**Benchmarking Harness:** `TournamentBenchmarkRunner`, `FairTournament50MRunner`, and `FinalReconciliationTournamentRunner`  
**Host Hardware:** AMD Ryzen 7 5825U (8 Physical Cores, 16 Logical Processors, 16 MB L3 Cache)  
**Execution Environment:** Windows 11 x64, .NET 10.0.401, PostgreSQL 17.2, 16 GB RAM  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## 1. Executive Summary

To evaluate all candidate representations on identical hardware and data distributions, candidate tournaments were conducted across:
- **Phase 1:** 1,000,000 synthetic accounts (uniform random, Zipfian skewed, contended concurrent writes).
- **Phase 2:** 10,000,000 synthetic accounts (verifying scalability and cache boundary effects).
- **Phase 3:** Full 50,000,000 production dataset (`accounts_50m`) on AMD Ryzen 7 5825U.
- **Phase 4 (Final Reconciliation):** Exhaustive 50M verification of the integrated production candidate `PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001` alongside the 6 research baselines, with timer quantization resolved via 100-query batch sampling.

All candidates were populated using the empirical 50M distribution:
- **45.0005%** Pattern 128 (Active, bal < 100)
- **45.0003%** Pattern 193 (Active, bal >= 100)
- **4.9995%** Pattern 0 (Inactive, bal < 100)
- **4.9995%** Pattern 1 (Inactive, bal >= 100)
- Plus 113 rare exception accounts (Patterns 251, 243, 255, 3).

---

## 2. 50,000,000 Record Finalist Tournament Results (Reconciled)

Conducted across 50,000,000 records with single-thread read, 4-thread read, 8-thread read, 16-thread saturation read, Zipfian (80/20) read, sequential full scan, single-thread write, multi-thread write, adjacent packed-ID contention write, and mixed concurrent workloads:

| Candidate ID | Candidate Name | Architecture Family | Total Static (MB) | Total Static (MiB) | Bits / Acc | ST Read (Mops/s) | ST Mean (ns) | ST p50 (ns) | ST p99 (ns) | MT 8T (Mops/s) | MT 16T (Mops/s) | Zipf 8T (Mops/s) | Seq Scan (Mops/s) | ST Write (Mops/s) | MT 8T Write (Mops/s) | Adj CAS Write (Mops/s) | Mixed 95/5 (Mops/s) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `SEM-CLASS3-004` | 3-Bit Semantic Classes | SemanticEncoding | **18.75** | **17.88** | **3.00** | 8.48 | 117.9 | 94.0 | 296.0 | 78.09 | 96.97 | 106.14 | 103.69 | 4.96 | 16.76 | 32.22 | 92.10 |
| `COMBO-SEM-SPARSE-002` | Semantic Classes + Rare Exceptions | HybridCompositions | 25.00 | 23.84 | 4.00 | 26.88 | 37.2 | 40.0 | 55.0 | 67.04 | 81.18 | 98.75 | 82.00 | 14.71 | 7.16 | 9.84 | 36.07 |
| `SEM-CLASS4-003` | 4-Bit Semantic Classes | SemanticEncoding | 25.00 | 23.84 | 4.00 | **37.85** | **26.4** | **21.0** | **60.0** | 88.73 | 97.03 | 100.91 | 210.18 | **31.67** | **61.31** | **182.40** | **103.97** |
| **`PROD-01` (Integrated)** | **4-Bit Exception Seqlock** | **ProductionCandidate** | **26.95** | **25.70** | **4.31** | **16.50** | **60.6** | **53.0** | **148.0** | **62.76** | **85.07** | **93.58** | **60.87** | **11.11** | **9.12** | **9.72** | **32.91** |
| `REP-CJB-001` | Correlated Outcome Joint Bitmap | Representations | 50.00 | 47.68 | 8.00 | 33.64 | 29.7 | 22.0 | 89.0 | 90.00 | 115.94 | 114.13 | 138.21 | 48.80 | 59.59 | 171.50 | 96.83 |
| `SEM-BYTEVEC-002` | Byte Answer Vector | SemanticEncoding | 50.00 | 47.68 | 8.00 | 38.10 | 26.2 | 24.0 | 43.0 | 218.95 | 71.91 | 117.45 | 295.77 | 51.98 | 95.24 | 299.45 | 371.95 |
| `SEM-BITMAPS-001` | Independent Bitmaps (EAP Baseline) | SemanticEncoding | 50.00 | 47.68 | 8.00 | 10.74 | 93.1 | 94.0 | 248.0 | 78.60 | 105.78 | 103.46 | 95.18 | 2.80 | 9.64 | 69.25 | 306.45 |

*(Note: `PROD-01` is `PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001`)*

---

## 3. 10,000,000 Account Tournament Results (Phase 2)

| Rank | Candidate ID | Architecture Name | Family | Memory (MB) | Memory (MiB) | Bits / Acc | ST Read (Mops/s) | MT Read (Mops/s) | Zipf Read (Mops/s) | MT Write (Mops/s) |
|---|---|---|---|---|---|---|---|---|---|---|
| **1** | `SEM-CLASS3-004` | 3-Bit Semantic Classes | SemanticEncoding | **3.75** | **3.58** | **3.00** | 15.06 | 71.43 | 65.96 | 16.91 |
| **2** | `COMBO-SEM-SPARSE-002` | Semantic Classes + Rare Exceptions | HybridCompositions | **5.00** | **4.77** | **4.00** | 22.19 | 75.15 | 67.44 | 8.33 |
| **3** | `SEM-CLASS4-003` | 4-Bit Semantic Classes | SemanticEncoding | **5.00** | **4.77** | **4.00** | 27.81 | **84.04** | 69.68 | 34.74 |
| 4 | `REP-FSCG-002` | Factorized Shared Condition Graph | Representations | 7.50 | 7.15 | 6.00 | 4.24 | 36.41 | 36.84 | 19.17 |
| 5 | `COMBO-ATOMS-BATCH-004` | Shared Atoms + Batch SIMD Reuse | HybridCompositions | 7.50 | 7.15 | 6.00 | 7.36 | 59.90 | 56.12 | 6.94 |
| 6 | `COMBO-FSCG-DARG-001` | Factorized Atoms + DARG + Epoch | HybridCompositions | 7.50 | 7.15 | 6.00 | 3.62 | 32.86 | 36.68 | 4.96 |
| 7 | `SEM-BITMAPS-001` | Independent Bitmaps (EAP Baseline) | SemanticEncoding | 10.00 | 9.54 | 8.00 | 28.92 | 62.80 | 68.58 | 10.13 |
| 8 | `SEM-BYTEVEC-002` | Byte Answer Vector | SemanticEncoding | 10.00 | 9.54 | 8.00 | **62.85** | **121.50** | 23.75 | 54.77 |
| 9 | `REP-CJB-001` | Correlated Outcome Joint Bitmap | Representations | 10.00 | 9.54 | 8.00 | 59.23 | **121.03** | **107.20** | **55.14** |
| 10 | `REC-COW-001` | Copy-On-Write Answer Pages | Recovery | 10.02 | 9.56 | 8.02 | 18.69 | 70.66 | 80.45 | 0.40 |
| 11 | `REC-SEQLOCK-003` | Seqlock Page Publication | Recovery | 10.16 | 9.69 | 8.13 | 24.98 | 72.35 | 77.78 | 7.34 |
| 12 | `REC-CCPR-002` | Checkpointed Prefix Replay | Recovery | 20.00 | 19.07 | 16.00 | 46.72 | 92.83 | 96.33 | 1.30 |
| 13 | `COMBO-PORTFOLIO-006` | Complete Portfolio Router | HybridCompositions | 25.00 | 23.84 | 20.00 | 17.03 | 64.70 | 57.46 | 1.39 |
| 14 | `COMBO-OVERLAY-COW-003` | Bounded Overlay + CoW Generations | HybridCompositions | 291.00 | 277.52 | 232.80 | 4.25 | 18.57 | 38.72 | 9.66 |
| 15 | `COMBO-HOT-COLD-005` | Hot Materialization + Cold Fallback | HybridCompositions | 320.00 | 305.18 | 256.00 | 6.60 | 18.84 | 16.46 | 10.51 |

---

## 4. Key Performance Insights & Hardware Reality

1. **Hardware Cache Boundary Clarification:**  
   The AMD Ryzen 7 5825U host has **16 MB of shared L3 cache**. A 25.00 MB array does not fit entirely in L3; rather, L3 cache covers **64.0%** of the working set (vs 32% for 50 MB arrays). 4-bit delivers a 2x higher L3 hit rate, cuts DDR4 bus traffic in half, and packs **128 accounts per 64-byte cache line** (vs 64 accounts in 8-bit).
2. **Resolution of Latency Timer Quantization:**  
   The 100 ns p50 reported in earlier phases was an artifact of the Windows 10 MHz timer quantum. With 100-query batch sampling, `SEM-CLASS4-003` achieves **21.0 ns p50** (26.4 ns mean), while `PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001` achieves **53.0 ns p50** (60.6 ns mean).
3. **Seqlock Concurrency Trade-Off:**  
   `PROD-01` incorporates 48,829 seqlock page sequences and page monitor locks (1.91 MB metadata). This reduces single-thread read throughput from 37.85 Mops/s to 16.50 Mops/s, but provides 100% linearizable tear-free reads across dense nibbles and sparse exception tables under continuous concurrent writes.
4. **Definitive Production Recommendation:**  
   Deploy **`PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001`**. It satisfies all 10 production gates with 100% answer parity, 26.95 MB memory footprint, and >85 Mops/s multi-threaded throughput.
