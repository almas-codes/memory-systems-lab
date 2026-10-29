# Candidate Tournament Benchmark Results (10,000,000 Accounts)

**Date:** 2026-10-29 10:04:32 UTC  
**Threads:** 8 | **Iterations:** 500,000 per thread  

| Candidate ID | Candidate Name | Family | Memory (MB) | Memory (MiB) | Bits/Acc | ST Read (Mops/s) | MT Read (Mops/s) | Zipf Read (Mops/s) | MT Write (Mops/s) |
|---|---|---|---|---|---|---|---|---|---|
| `SEM-CLASS3-004` | 3-Bit Semantic Classes | SemanticEncoding | 3.75 | 3.58 | 3.00 | 15.06 | 71.43 | 65.96 | 16.91 |
| `COMBO-SEM-SPARSE-002` | Semantic Classes + Rare Exceptions | HybridCompositions | 5.00 | 4.77 | 4.00 | 22.19 | 75.15 | 67.44 | 8.33 |
| `SEM-CLASS4-003` | 4-Bit Semantic Classes | SemanticEncoding | 5.00 | 4.77 | 4.00 | 27.81 | 84.04 | 69.68 | 34.74 |
| `REP-FSCG-002` | Factorized Shared Condition Graph | Representations | 7.50 | 7.15 | 6.00 | 4.24 | 36.41 | 36.84 | 19.17 |
| `COMBO-ATOMS-BATCH-004` | Shared Atoms + Batch SIMD Reuse | HybridCompositions | 7.50 | 7.15 | 6.00 | 7.36 | 59.90 | 56.12 | 6.94 |
| `COMBO-FSCG-DARG-001` | Factorized Atoms + DARG + Epoch | HybridCompositions | 7.50 | 7.15 | 6.00 | 3.62 | 32.86 | 36.68 | 4.96 |
| `SEM-BITMAPS-001` | Independent Bitmaps (EAP Baseline) | SemanticEncoding | 10.00 | 9.54 | 8.00 | 28.92 | 62.80 | 68.58 | 10.13 |
| `SEM-BYTEVEC-002` | Byte Answer Vector | SemanticEncoding | 10.00 | 9.54 | 8.00 | 62.85 | 121.50 | 23.75 | 54.77 |
| `REP-CJB-001` | Correlated Outcome Joint Bitmap | Representations | 10.00 | 9.54 | 8.00 | 59.23 | 121.03 | 107.20 | 55.14 |
| `REC-COW-001` | Copy-On-Write Answer Pages | Recovery | 10.02 | 9.56 | 8.02 | 18.69 | 70.66 | 80.45 | 0.40 |
| `REC-SEQLOCK-003` | Seqlock Page Publication | Recovery | 10.16 | 9.69 | 8.13 | 24.98 | 72.35 | 77.78 | 7.34 |
| `REC-CCPR-002` | Checkpointed Prefix Replay | Recovery | 20.00 | 19.07 | 16.00 | 46.72 | 92.83 | 96.33 | 1.30 |
| `COMBO-PORTFOLIO-006` | Complete Portfolio Router | HybridCompositions | 25.00 | 23.84 | 20.00 | 17.03 | 64.70 | 57.46 | 1.39 |
| `COMBO-OVERLAY-COW-003` | Bounded Overlay + CoW Generations | HybridCompositions | 291.00 | 277.52 | 232.80 | 4.25 | 18.57 | 38.72 | 9.66 |
| `COMBO-HOT-COLD-005` | Hot Materialization + Exact Cold Fallback | HybridCompositions | 320.00 | 305.18 | 256.00 | 6.60 | 18.84 | 16.46 | 10.51 |
