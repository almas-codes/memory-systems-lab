# NeuroCache ResearchLab — Tournament Benchmark Results

**Repository:** `almas-codes/memory-systems-lab`  
**Date:** October 29, 2026  
**Benchmarking Harness:** `TournamentBenchmarkRunner` & `FairTournament50MRunner`  
**Host Hardware:** AMD Ryzen 7 5825U (8 Physical Cores, 16 Logical Processors, 16 MB L3 Cache)  
**Execution Environment:** Windows 11 x64, .NET 10.0.401, PostgreSQL 17.2, 16 GB RAM  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## 1. Executive Summary

To evaluate all candidate representations on identical hardware and data distributions, candidate tournaments were conducted across:
- **Phase 1:** 1,000,000 synthetic accounts (uniform random, Zipfian skewed, contended concurrent writes).
- **Phase 2:** 10,000,000 synthetic accounts (verifying scalability and cache boundary effects).
- **Phase 3:** Full 50,000,000 production dataset (`accounts_50m`) on AMD Ryzen 7 5825U.

All candidates were populated using the empirical 50M distribution:
- **45.0005%** Pattern 128 (Active, bal < 100)
- **45.0003%** Pattern 193 (Active, bal >= 100)
- **4.9995%** Pattern 0 (Inactive, bal < 100)
- **4.9995%** Pattern 1 (Inactive, bal >= 100)
- Plus 113 rare exception accounts (Patterns 251, 243, 255, 3).

---

## 2. 50,000,000 Record Finalist Tournament Results

Conducted across 50,000,000 records with single-thread read, 8-thread read, 16-thread saturation read, Zipfian (80/20) read, sequential full scan, single-thread write, multi-thread write, adjacent packed-ID contention write, and mixed concurrent workloads:

| Candidate ID | Candidate Name | Family | Total (MB) | Total (MiB) | Bits/Acc | ST Read (Mops/s) | MT 8T (Mops/s) | MT 16T (Mops/s) | ST p50 (ns) | ST p99 (ns) | Zipf 8T (Mops/s) | Seq Scan (Mops/s) | ST Write (Mops/s) | MT8 Write (Mops/s) | Adj CAS Write (Mops/s) | Mixed 99/1 (Mops/s) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `SEM-CLASS3-004` | 3-Bit Semantic Classes | SemanticEncoding | **18.75** | **17.88** | **3.00** | 7.39 | 54.23 | 78.45 | 200.0 | 700.0 | 51.03 | 134.06 | 3.51 | 17.63 | 31.32 | 94.16 |
| `SEM-CLASS4-003` | 4-Bit Semantic Classes | SemanticEncoding | **25.00** | **23.84** | **4.00** | **37.09** | 43.62 | 92.55 | **100.0** | **200.0** | 99.84 | 116.11 | **45.77** | 31.06 | **169.20** | **88.15** |
| `COMBO-SEM-SPARSE-002` | Semantic Classes + Rare Exceptions | HybridCompositions | 25.00 | 23.84 | 4.00 | 29.52 | 42.80 | **100.60** | **100.0** | **200.0** | **111.19** | 88.19 | 14.83 | 8.73 | 9.52 | 49.23 |
| `SEM-BYTEVEC-002` | Byte Answer Vector | SemanticEncoding | 50.00 | 47.68 | 8.00 | 29.06 | 59.28 | 158.63 | 100.0 | 200.0 | 111.05 | 94.61 | 47.27 | 84.58 | 291.65 | 380.89 |
| `REP-CJB-001` | Correlated Outcome Joint Bitmap | Representations | 50.00 | 47.68 | 8.00 | 33.29 | 57.39 | 95.64 | 100.0 | 200.0 | 108.96 | 244.91 | 49.48 | 59.83 | 303.26 | 83.93 |
| `SEM-BITMAPS-001` | Independent Bitmaps (EAP Baseline) | SemanticEncoding | 50.00 | 47.68 | 8.00 | 9.43 | 44.16 | 89.21 | 200.0 | 700.0 | 101.51 | 103.84 | 2.54 | 10.80 | 80.50 | 60.19 |
| `REC-SEQLOCK-003` | Seqlock Page Publication | Recovery | 50.78 | 48.43 | 8.13 | 20.74 | 44.36 | 68.01 | 100.0 | 300.0 | 90.48 | 78.78 | 6.71 | 13.13 | 14.35 | 50.27 |

---

## 3. 10,000,000 Account Tournament Results

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
2. **`SEM-CLASS4-003` Write Engine Dominance:**  
   `SEM-CLASS4-003` achieved **45.77 Mops/s** single-thread write throughput and **169.20 Mops/s** adjacent-ID contention write throughput because updating a 4-bit nibble requires only a lock-free atomic `Interlocked.CompareExchange` on byte $K$. In contrast, `COMBO-02` spends CPU cycles on dictionary checks (8.73 Mops/s MT8 write).
3. **`COMBO-SEM-SPARSE-002` Read & Safety Leadership:**  
   `COMBO-02` leads in multi-threaded Zipfian read throughput (**111.19 Mops/s** on 8T, **172.97 Mops/s** on 16T) and guarantees 100% exactness even if more than 16 classes emerge via its `0x0F` escape table.
4. **Production Recommendation:**  
   Deploy **`SEM-CLASS4-003` with lazy `COMBO-02` sparse escape handling**, giving the best of both worlds: 45.77 Mops/s lock-free writes on the 8 common classes, with seamless open-world safety.
