# NeuroCache ResearchLab — Comprehensive Experiment Matrix

**Repository:** `almas-codes/memory-systems-lab`  
**Date:** October 29, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## 1. Multi-Dimensional Candidate Evaluation Matrix

The table below contrasts the 15 primary tournament candidates benchmarked directly on 10,000,000 records on .NET 10.0 x64, with memory projections scaled to the full 50,000,000 record production dataset.

| Candidate ID | Architecture Name | Family | Memory 10M (MB / MiB) | Memory 50M (MB / MiB) | Bits / Acc | ST Read (Mops/s) | MT Read (Mops/s) | Zipf Read (Mops/s) | MT Write (Mops/s) | Dynamic Reg? | Consistency / Tear-Free |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **`SEM-CLASS3-004`** | 3-Bit Semantic Classes | SemanticEncoding | 3.75 / 3.58 | **18.75 / 17.88** | **3.00** | 15.06 | 71.43 | 65.96 | 16.91 | No | Striped lock write |
| **`COMBO-SEM-SPARSE-002`** | Semantic Classes + Rare Exceptions | HybridCompositions | 5.00 / 4.77 | **25.00 / 23.84** | **4.00** | 22.19 | 75.15 | 67.44 | 8.33 | No | Atomic CAS nibble |
| **`SEM-CLASS4-003`** | 4-Bit Semantic Classes | SemanticEncoding | 5.00 / 4.77 | **25.00 / 23.84** | **4.00** | 27.81 | **84.04** | 69.68 | 34.74 | No | Lock-free CAS |
| `REP-FSCG-002` | Factorized Shared Condition Graph | Representations | 7.50 / 7.15 | 37.50 / 35.76 | 6.00 | 4.24 | 36.41 | 36.84 | 19.17 | **Yes** | Bitplane isolation |
| `COMBO-ATOMS-BATCH-004` | Shared Atoms + Batch SIMD Reuse | HybridCompositions | 7.50 / 7.15 | 37.50 / 35.76 | 6.00 | 7.36 | 59.90 | 56.12 | 6.94 | **Yes** | SIMD word CAS |
| `COMBO-FSCG-DARG-001` | Factorized Atoms + DARG + Epoch | HybridCompositions | 7.50 / 7.15 | 37.50 / 35.76 | 6.00 | 3.62 | 32.86 | 36.68 | 4.96 | **Yes** | 64-bit epoch boundary |
| `SEM-BITMAPS-001` | Independent Bitmaps (EAP Baseline) | SemanticEncoding | 10.00 / 9.54 | 50.00 / 47.68 | 8.00 | 28.92 | 62.80 | 68.58 | 10.13 | **Yes** | 1 bit / query |
| `SEM-BYTEVEC-002` | Byte Answer Vector | SemanticEncoding | 10.00 / 9.54 | 50.00 / 47.68 | 8.00 | **62.85** | **121.50** | 23.75 | 54.77 | No | Atomic byte write |
| `REP-CJB-001` | Correlated Outcome Joint Bitmap | Representations | 10.00 / 9.54 | 50.00 / 47.68 | 8.00 | 59.23 | **121.03** | **107.20** | **55.14** | No | Atomic byte write |
| `REC-COW-001` | Copy-On-Write Answer Pages | Recovery | 10.02 / 9.56 | 50.10 / 47.78 | 8.02 | 18.69 | 70.66 | 80.45 | 0.40 | No | Atomic pointer swap |
| **`REC-SEQLOCK-003`** | Seqlock Page Publication | Recovery | 10.16 / 9.69 | 50.80 / 48.45 | 8.13 | 24.98 | 72.35 | 77.78 | 7.34 | No | **Strict Tear-Free** |
| `REC-CCPR-002` | Checkpointed Prefix Replay | Recovery | 20.00 / 19.07 | 100.00 / 95.37 | 16.00 | 46.72 | 92.83 | 96.33 | 1.30 | No | Dual state + watermark |
| `COMBO-PORTFOLIO-006` | Complete Portfolio Router | HybridCompositions | 25.00 / 23.84 | 125.00 / 119.21 | 20.00 | 17.03 | 64.70 | 57.46 | 1.39 | **Yes** | Multi-engine sync |
| `COMBO-OVERLAY-COW-003` | Bounded Overlay + CoW Generations | HybridCompositions | 291.00 / 277.52 | 1,455 / 1,387 | 232.80 | 4.25 | 18.57 | 38.72 | 9.66 | No | Background drain |
| `COMBO-HOT-COLD-005` | Hot Materialization + Cold Fallback | HybridCompositions | 320.00 / 305.18 | 1,600 / 1,525 | 256.00 | 6.60 | 18.84 | 16.46 | 10.51 | No | On-demand compute |

---

## 2. Quantitative Trade-off Frontier

### 2.1. Memory Compression vs. Computational Complexity
```
  Memory (Bits/Acc)
     ▲
 256 ┼──────────────────────────────────────────────────────── COMBO-05 (Hot/Cold)
     │
  16 ┼────────────────────────────────────────── REC-02 (Dual Checkpoint)
     │
   8 ┼────────────────── SEM-01 (EAP Baseline), SEM-02, REP-01, REC-03 (Seqlock)
     │
   6 ┼────────── REP-02 (Factorized Atoms), COMBO-01, COMBO-04
     │
   4 ┼───── SEM-03 (4-bit Classes), COMBO-02 (Classes + Exceptions)  [SWEET SPOT]
     │
   3 ┼── SEM-04 (3-bit Packed Classes) [MAXIMUM COMPRESSION]
     │
   0 ┼─────┴──────────┴──────────┴──────────┴──────────┴──────────►
     0     20         40         60         80        100      120 MT Read Mops/s
```

### 2.2. Architectural Trade-off Observations
1. **The Sub-Byte Barrier:** Compressing below 8 bits/account requires either factorizing condition atoms (6 bits) or packing semantic equivalence classes (3–4 bits). 
2. **Nibble Packing vs. Bit-Packing:** `SEM-03` (4-bit nibbles) achieves 84.04 Mops/s MT read throughput because 2 nibbles per byte require only simple `>> 4` or `& 0x0F` masking with zero byte-straddling logic. In contrast, `SEM-04` (3-bit) requires cross-byte boundary assembly, slightly reducing MT read throughput to 71.43 Mops/s while saving an additional 25% of memory.
3. **The Tear-Free Cost:** Adding 64-bit sequence counters (`REC-03`) increases memory by a negligible 0.13 bits/account (+1.6%) while providing complete mathematical immunity against torn read anomalies during concurrent multi-byte publications.
