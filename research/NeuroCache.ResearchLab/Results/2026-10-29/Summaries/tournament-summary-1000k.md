# Candidate Tournament Benchmark Results (1,000,000 Accounts)

**Date:** 2026-10-29 10:03:10 UTC  
**Threads:** 8 | **Iterations:** 1,000,000 per thread  

| Candidate ID | Candidate Name | Family | Memory (MB) | Memory (MiB) | Bits/Acc | ST Read (Mops/s) | MT Read (Mops/s) | Zipf Read (Mops/s) | MT Write (Mops/s) |
|---|---|---|---|---|---|---|---|---|---|
| `SEM-CLASS3-004` | 3-Bit Semantic Classes | SemanticEncoding | 0.38 | 0.36 | 3.00 | 18.09 | 88.38 | 72.14 | 15.76 |
| `COMBO-SEM-SPARSE-002` | Semantic Classes + Rare Exceptions | HybridCompositions | 0.50 | 0.48 | 4.00 | 22.20 | 86.69 | 68.80 | 8.23 |
| `SEM-CLASS4-003` | 4-Bit Semantic Classes | SemanticEncoding | 0.50 | 0.48 | 4.00 | 26.99 | 107.75 | 56.35 | 33.93 |
| `REP-FSCG-002` | Factorized Shared Condition Graph | Representations | 0.75 | 0.72 | 6.00 | 8.46 | 44.02 | 40.74 | 20.27 |
| `COMBO-ATOMS-BATCH-004` | Shared Atoms + Batch SIMD Reuse | HybridCompositions | 0.75 | 0.72 | 6.00 | 18.21 | 78.10 | 61.41 | 7.13 |
| `COMBO-FSCG-DARG-001` | Factorized Atoms + DARG + Epoch | HybridCompositions | 0.75 | 0.72 | 6.00 | 6.34 | 37.56 | 38.59 | 5.07 |
| `SEM-BITMAPS-001` | Independent Bitmaps (EAP Baseline) | SemanticEncoding | 1.00 | 0.95 | 8.00 | 36.39 | 93.69 | 85.33 | 10.13 |
| `SEM-BYTEVEC-002` | Byte Answer Vector | SemanticEncoding | 1.00 | 0.95 | 8.00 | 59.09 | 155.03 | 82.86 | 39.84 |
| `REP-CJB-001` | Correlated Outcome Joint Bitmap | Representations | 1.00 | 0.95 | 8.00 | 54.76 | 160.95 | 115.88 | 53.24 |
| `REC-COW-001` | Copy-On-Write Answer Pages | Recovery | 1.01 | 0.96 | 8.04 | 27.14 | 111.92 | 96.18 | 0.63 |
| `REC-SEQLOCK-003` | Seqlock Page Publication | Recovery | 1.02 | 0.97 | 8.13 | 25.26 | 98.33 | 87.46 | 6.28 |
| `REC-CCPR-002` | Checkpointed Prefix Replay | Recovery | 2.00 | 1.91 | 16.00 | 47.45 | 141.13 | 118.66 | 1.36 |
| `COMBO-PORTFOLIO-006` | Complete Portfolio Router | HybridCompositions | 2.50 | 2.38 | 20.00 | 15.91 | 77.90 | 64.30 | 1.46 |
| `COMBO-OVERLAY-COW-003` | Bounded Overlay + CoW Generations | HybridCompositions | 29.13 | 27.78 | 233.07 | 5.96 | 26.82 | 48.09 | 8.44 |
| `COMBO-HOT-COLD-005` | Hot Materialization + Exact Cold Fallback | HybridCompositions | 32.00 | 30.52 | 256.00 | 7.13 | 5.35 | 32.52 | 9.62 |
