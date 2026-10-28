# NeuroCache.ResearchLab Experiment Matrix

## 1. Workload Access Distributions

| Access Distribution | Working Set | Pattern Characteristics |
|---|---|---|
| **Uniform Random** | 100,000,000 keys | Pure random access across the entire 100M key space; zero locality. |
| **Zipf Skewed (85/15)** | 1,000,000 hot keys (1%) | 85% of queries target 1% of the records; 15% target cold records. |
| **Sequential Scan** | Linear sweep | Sequential ascending key access testing prefetching and OS readahead. |
| **WorkingSetFit** | 20,000 keys | Entire working set fits in a tight 32MB - 64MB RAM budget. |
| **WorkingSet2xRAM** | 1,000,000 keys | Working set is 2x the standard RAM budget. |
| **WorkingSet10xRAM** | 10,000,000 keys | Working set is 10x the standard RAM budget. |
| **Bursty Access** | Clusters of 50 keys | Repeated micro-bursts against localized hot spots. |
| **Adversarial Stride** | Prime stride (104,729) | Stride designed to defeat naive sequential prefetching and cache lines. |

---

## 2. Read / Write Mutation Ratios

- **100% Read**: Pure retrieval workload.
- **99% Read / 1% Write**: Low-churn background update workload.
- **95% Read / 5% Write**: Standard production enterprise application workload.
- **80% Read / 20% Write**: Moderate-churn OLTP mixed workload.
- **50% Read / 50% Write**: High-churn stress test for invalidation mechanisms.

---

## 3. Request Semantics

- **ExactGet**: Full payload retrieval.
- **FieldProjection**: Extracted scalar field (Field1 or Field2) without reading the entire payload.
- **ComputeDerived**: Pure mathematical transformation (`(Field1 * 31 + Param) ^ 0x5555...`).
- **EvaluatePredicate**: Deterministic boolean filter (`Field1 > Threshold`).
- **Upsert**: Mutation incrementing record generation and State Witness epoch.
- **Invalidate**: Explicit key or domain invalidation.

---

## 4. Benchmark Suites

1. **Smoke Suite**: Fast verification across all 8 strategies on sample data.
2. **Micro Suite**: Controlled microbenchmarks comparing:
   - Pure Recomputation vs NVMe SSD Read
   - Single-Read Capsule vs Direct Extent Read
3. **Full Matrix Suite**: 8 Access distributions x Varying Read/Write mixtures across all strategies.
4. **Budget Suite**: Strict physical RAM constraints: 128 MB, 256 MB, 512 MB, 1024 MB.
5. **Ablation Suite**: NeuroCache Ω with components selectively isolated:
   - Full (Witnesses + Recomputation + Capsules)
   - No Witnesses
   - No Recomputation
   - No Capsules
   - Minimal
6. **Adversarial Suite**: Cache defeat stride & 50% write churn stress tests.
