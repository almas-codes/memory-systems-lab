# NeuroCache ResearchLab — Tournament Benchmark Results

**Repository:** `almas-codes/memory-systems-lab`  
**Date:** October 29, 2026  
**Benchmarking Harness:** `TournamentBenchmarkRunner`  
**Execution Environment:** Windows 11 x64, .NET 10.0.100, 8 Logical Cores, 16 GB RAM  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## 1. Executive Summary

To evaluate all candidate representations on identical hardware and data distributions, a tournament benchmark was conducted across 15 candidate architectures on:
- **Phase 1:** 1,000,000 synthetic accounts (uniform random, Zipfian skewed, contended concurrent writes).
- **Phase 2:** 10,000,000 synthetic accounts (verifying scalability and cache boundary effects).
- **Phase 3:** Analytical projection to the full 50,000,000 production dataset (`accounts_50m`).

All candidates were populated using the empirical 50M distribution:
- **45%** Pattern 128 (Active, bal < 100)
- **45%** Pattern 193 (Active, bal >= 100)
- **5%** Pattern 0 (Inactive, bal < 100)
- **5%** Pattern 1 (Inactive, bal >= 100)

---

## 2. 10,000,000 Account Tournament Results

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

## 3. 1,000,000 Account Tournament Results

| Rank | Candidate ID | Architecture Name | Family | Memory (MB) | Memory (MiB) | Bits / Acc | ST Read (Mops/s) | MT Read (Mops/s) | Zipf Read (Mops/s) | MT Write (Mops/s) |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `SEM-CLASS3-004` | 3-Bit Semantic Classes | SemanticEncoding | 0.38 | 0.36 | 3.00 | 18.09 | 88.38 | 72.14 | 15.76 |
| 2 | `COMBO-SEM-SPARSE-002` | Semantic Classes + Rare Exceptions | HybridCompositions | 0.50 | 0.48 | 4.00 | 22.20 | 86.69 | 68.80 | 8.23 |
| 3 | `SEM-CLASS4-003` | 4-Bit Semantic Classes | SemanticEncoding | 0.50 | 0.48 | 4.00 | 26.99 | 107.75 | 56.35 | 33.93 |
| 4 | `REP-FSCG-002` | Factorized Shared Condition Graph | Representations | 0.75 | 0.72 | 6.00 | 8.46 | 44.02 | 40.74 | 20.27 |
| 5 | `COMBO-ATOMS-BATCH-004` | Shared Atoms + Batch SIMD Reuse | HybridCompositions | 0.75 | 0.72 | 6.00 | 18.21 | 78.10 | 61.41 | 7.13 |
| 6 | `COMBO-FSCG-DARG-001` | Factorized Atoms + DARG + Epoch | HybridCompositions | 0.75 | 0.72 | 6.00 | 6.34 | 37.56 | 38.59 | 5.07 |
| 7 | `SEM-BITMAPS-001` | Independent Bitmaps (EAP Baseline) | SemanticEncoding | 1.00 | 0.95 | 8.00 | 36.39 | 93.69 | 85.33 | 10.13 |
| 8 | `SEM-BYTEVEC-002` | Byte Answer Vector | SemanticEncoding | 1.00 | 0.95 | 8.00 | 59.09 | 155.03 | 82.86 | 39.84 |
| 9 | `REP-CJB-001` | Correlated Outcome Joint Bitmap | Representations | 1.00 | 0.95 | 8.00 | 54.76 | 160.95 | 115.88 | 53.24 |
| 10 | `REC-COW-001` | Copy-On-Write Answer Pages | Recovery | 1.01 | 0.96 | 8.04 | 27.14 | 111.92 | 96.18 | 0.63 |
| 11 | `REC-SEQLOCK-003` | Seqlock Page Publication | Recovery | 1.02 | 0.97 | 8.13 | 25.26 | 98.33 | 87.46 | 6.28 |
| 12 | `REC-CCPR-002` | Checkpointed Prefix Replay | Recovery | 2.00 | 1.91 | 16.00 | 47.45 | 141.13 | 118.66 | 1.36 |
| 13 | `COMBO-PORTFOLIO-006` | Complete Portfolio Router | HybridCompositions | 2.50 | 2.38 | 20.00 | 15.91 | 77.90 | 64.30 | 1.46 |
| 14 | `COMBO-OVERLAY-COW-003` | Bounded Overlay + CoW Generations | HybridCompositions | 29.13 | 27.78 | 233.07 | 5.96 | 26.82 | 48.09 | 8.44 |
| 15 | `COMBO-HOT-COLD-005` | Hot Materialization + Cold Fallback | HybridCompositions | 32.00 | 30.52 | 256.00 | 7.13 | 5.35 | 32.52 | 9.62 |

---

## 4. Key Performance Insights

1. **Memory Compression Champion:**
   `SEM-CLASS3-004` (3-Bit Semantic Classes) achieves **3.00 bits per account**, which is **62.5% smaller** than the 8-bit baseline (3.75 MB vs 10.00 MB on 10M, and 18.75 MB vs 50.00 MB on 50M).
   Even with bit-shift overhead crossing byte boundaries, it sustains **71.43 Mops/s** multi-threaded read throughput and **16.91 Mops/s** write throughput.

2. **Overall Architecture Champion:**
   `SEM-CLASS4-003` (4-Bit Semantic Classes) achieves **4.00 bits per account** (5.00 MB on 10M, 25.00 MB on 50M — a **50% memory reduction**), while achieving **84.04 Mops/s** multi-threaded read throughput and **34.74 Mops/s** write throughput! Because each byte holds exactly two 4-bit nibbles, bitfield extraction is simple and fast (`pair & 0x0F` or `pair >> 4`).

3. **Hybrid Robustness Champion:**
   `COMBO-SEM-SPARSE-002` (Semantic Classes with Sparse Rare Exceptions) matches the 4.00 bits/account footprint while providing mathematically guaranteed exactness for any rare outlier patterns, delivering **75.15 Mops/s** MT read throughput.

4. **Raw Throughput Champions:**
   `REP-CJB-001` (Joint Bitmap) and `SEM-BYTEVEC-002` (Byte Vector) lead raw throughput at **121+ Mops/s** MT reads and **55 Mops/s** writes, but require 8 bits per account (50 MB on 50M records).

5. **Tear-Free Publication Champion:**
   `REC-SEQLOCK-003` (Seqlock Page Publication) adds only **0.13 bits/account** of sequence metadata while completely preventing torn reads under concurrent writes, delivering **72.35 Mops/s** MT read throughput.
