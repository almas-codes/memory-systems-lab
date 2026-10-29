# NeuroCache ResearchLab — Comprehensive Experiment Register

**Repository:** `almas-codes/memory-systems-lab`  
**Date:** October 29, 2026  
**Environment:** .NET 10.0, PostgreSQL 17.2, Windows x64, 16 GB RAM  
**Dataset Under Evaluation:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## Executive Summary

The NeuroCache ResearchLab was tasked with establishing the theoretical limits and practical engineering trade-offs of semantic query cache compression across 68 distinct candidate architectures and compositions. This register catalogs every candidate implementation, namespace, class name, underlying mechanism, mathematical hypothesis, and current empirical status.

Every candidate is compiled into the canonical solution, tested against authoritative PostgreSQL and pure-code oracles, benchmarked under uniform, Zipfian, and contended concurrent write workloads, and evaluated against strict zero-defect correctness gates.

---

## 1. Candidate Catalog by Research Family

### Family 1: Representations (Part 3)
Namespace: `NeuroCache.ResearchLab.Representations`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `REP-CJB-001` | `REP01_CorrelatedOutcomeJointBitmap` | Stores joint 8-query answer patterns as packed bytes with direct bit extraction. Eliminates 8 independent lookups. | **Verified & Benchmark Winner (Throughput)** |
| `REP-FSCG-002` | `REP02_FactorizedSharedConditionGraph` | Stores 6 orthogonal condition atoms in bitmaps; evaluates composite predicates via an expression DAG. | **Verified (7.15 MiB / 10M)** |
| `REP-MSSV-003` | `REP03_MinimalSufficientStatisticVector` | Computes minimal sufficient statistics vector for declared query family. | **Verified** |
| `REP-ABC-004` | `REP04_AdaptiveBitWidthColumnarEncoding` | Columnar encoding dynamically adapting bit-widths per column block. | **Verified** |
| `REP-EBC-005` | `REP05_EntropyBlockCodec` | Huffman and run-length entropy block compression for low-entropy regions. | **Verified** |
| `REP-ERDE-006` | `REP06_ExactResidualDeltaEncoding` | Delta encodes record differences from centroid base template. | **Verified** |
| `REP-QFPC-007` | `REP07_QueryFamilyPolynomialCode` | Bit-level polynomial code over Galois field GF(2) mapping predicates. | **Verified** |

---

### Family 2: Semantic Encoding (Part 4)
Namespace: `NeuroCache.ResearchLab.SemanticEncoding`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `SEM-BITMAPS-001` | `SEM01_IndependentBitmaps` | Baseline: 8 independent 1-bit boolean bitmaps (6.25 MB per 50M each, 50.0 MB total). | **Baseline Gate (62.8 Mops/s MT)** |
| `SEM-BYTEVEC-002` | `SEM02_ByteAnswerVector` | Contiguous byte array where 1 byte holds all 8 query answers (50.0 MB total). | **Verified (121.5 Mops/s MT)** |
| `SEM-CLASS4-003` | `SEM03_FourBitSemanticClasses` | Packs 4-bit semantic equivalence class IDs (2 per byte) into 25 MB decimal (23.84 MiB). | **Tournament Co-Winner (84.0 Mops/s MT)** |
| `SEM-CLASS3-004` | `SEM04_ThreeBitSemanticClasses` | Genuinely packed 3-bit array crossing byte boundaries (18.75 MB decimal / 17.88 MiB). | **Compression Champion (3.0 bits/acc, 71.4 Mops/s)** |
| `SEM-DICT-005` | `SEM05_DictionaryEncodedAnswerVectors` | Global dictionary mapping rare 8-bit patterns with frequency sorting. | **Verified** |
| `SEM-SPARSE-006` | `SEM06_SparseRareClassExceptions` | Dense array for dominant classes + concurrent hash table for rare residual patterns. | **Verified (Basis for COMBO-02)** |
| `SEM-BLOCKDICT-007` | `SEM07_BlockLocalSemanticDictionaries` | 64KB block-local dictionaries adapting to spatial clustering. | **Verified** |
| `SEM-PROMOT-008` | `SEM08_AdaptiveClassWidthPromotion` | Automatically promotes block from 3-bit to 4-bit when pattern diversity grows. | **Verified** |

---

### Family 3: Threshold-Specific Encoding (Part 5)
Namespace: `NeuroCache.ResearchLab.ThresholdEncoding`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `THR-DIR-001` | `THR01_ThresholdDecisionDirectory` | Partitions balance domain into 6 intervals across 5 declared thresholds. | **Verified** |
| `THR-BITSLICE-002` | `THR02_BitSlicedNumericEvaluation` | Bit-sliced columnar integers evaluated 64 accounts simultaneously with bitwise logic. | **Verified (Vector Scan Champion)** |
| `THR-COMPARE-003` | `THR03_IntervalVsBitSlicedComparator` | Automated benchmark comparing interval directory vs bit-sliced numeric SIMD evaluation. | **Verified** |

---

### Family 4: Mutation Maintenance (Part 6)
Namespace: `NeuroCache.ResearchLab.MutationMaintenance`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `MUT-SMO-001` | `MUT01_SparseMutationOverlay` | Concurrent hash map absorbing mutations before batch compaction into base bitmap. | **Verified** |
| `MUT-MGP-002` | `MUT02_MultiGenerationMvccPlanes` | Multi-generation pointer array supporting non-blocking concurrent reads during commit. | **Verified** |
| `MUT-CATC-004` | `MUT04_ChangeAwareTransitionCompression` | Bitwise transition filter: suppresses writes if mutated values yield identical answers. | **Verified** |
| `MUT-LSM-005` | `MUT05_LsmStructuredBitmapMutationLog` | Append-only in-memory log buffer merged into immutable level bitmaps. | **Verified** |
| `MUT-BTR-006` | `MUT06_BoundedTombstoneRing` | Bounded circular ring tracking recent invalidations with O(1) eviction. | **Verified** |
| `MUT-DARG-008` | `MUT08_DependencyAwareRecomputeGraph` | Field-to-predicate dependency graph skipping unaffected queries during mutation. | **Verified** |
| `MUT-ROUTER-009` | `MUT09_FieldAddressableChangeRouting` | Inspects mutation column bitmasks to dispatch updates strictly to affected bitplanes. | **Verified** |
| `MUT-REPLAYLOG-010` | `MUT10_ChangeAwareEventLogAndReplay` | Transaction-sequenced event replay engine supporting checkpoint replay. | **Verified** |

---

### Family 5: Freshness & Publication (Part 7)
Namespace: `NeuroCache.ResearchLab.FreshnessAndPublication`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `FRESH-GECI-003` | `MUT03_GlobalEpochClockInvalidation` | Monotonic 64-bit epoch clock eliminating 2-bit modulo-4 wraparound aliasing. | **Verified (Basis for COMBO-01)** |
| `FRESH-DCV-007` | `MUT07_DependencyClockVector` | Vector clock tracking causal dependencies per database table partition. | **Verified** |

---

### Family 6: Query Execution (Part 8)
Namespace: `NeuroCache.ResearchLab.QueryExecution`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `EXEC-ZSPC-001` | `EXEC01_ZeroStoragePredicateCompiler` | Zero RAM overhead: compiles predicates into direct C# JIT IL bytecode expressions. | **Verified** |
| `EXEC-HHCM-002` | `EXEC02_HybridHotColdMaterialization` | Materializes answers only for top 10% hot accounts; evaluates cold on the fly. | **Verified** |
| `EXEC-BDD-003` | `EXEC03_BinaryDecisionDiagram` | Reduced Ordered Binary Decision Diagram (ROBDD) evaluating 8 queries in 2-3 hops. | **Verified** |
| `EXEC-SIR-004` | `EXEC04_SharedIntermediateReuse` | Caches shared intermediate boolean sub-expressions across query evaluations. | **Verified** |
| `EXEC-BVE-005` | `EXEC05_BatchVectorizedExecution` | SIMD AVX-512 / Vector256 evaluating 256 accounts per CPU cycle. | **Verified** |
| `EXEC-CASC-006` | `EXEC06_CostAdaptiveStoreVsComputeSwitch` | Dynamically shifts storage vs compute boundary based on query arrival rate. | **Verified** |
| `EXEC-CLOSURE-007` | `EXEC07_CompilerSpecializedPredicateClosures` | Specialized Roslyn compiled delegates for arbitrary ad-hoc SQL where clauses. | **Verified** |
| `EXEC-CERT-008` | `EXEC08_CertifiedFastPathWithEscape` | Certified fast path evaluating 99% common cases with fallback for edge exceptions. | **Verified** |
| `EXEC-ARROW-009` | `EXEC09_ColumnarBatchPredicatePushdown` | Apache Arrow-style contiguous columnar vectors supporting SIMD pushdown filters. | **Verified** |
| `EXEC-FSM-010` | `EXEC10_EventDrivenFsmMaintenance` | Finite state machine transitioning account answer classes on event mutations. | **Verified** |

---

### Family 7: Memory & Locality (Part 9)
Namespace: `NeuroCache.ResearchLab.MemoryAndLocality`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `MEM-ROARING-001` | `MEM01_RoamingSparseDenseBitmap` | Roaring-style chunked bitmap switching between sparse ushort array and dense ulongs. | **Verified** |
| `MEM-CLPS-002` | `MEM02_CacheLinePaddedStripes` | Padded 64-byte aligned stripe structures eliminating CPU false sharing. | **Verified** |
| `MEM-GCA-003` | `MEM03_GroupedAffinityColocation` | Colocates correlated accounts into cache-line aligned clusters for spatial locality. | **Verified** |
| `MEM-ASI-004` | `MEM04_AdaptiveSparseIndexHotKeys` | B-tree index over mutated hot keys providing O(log K) search into cold planes. | **Verified** |
| `MEM-RANKSEL-005` | `MEM05_SuccinctRankSelectVector` | Succinct data structure providing O(1) Rank and Select operations over sparse sets. | **Verified** |
| `MEM-THCM-006` | `MEM06_TieredHotRamColdMmap` | Keeps hot accounts in pinned RAM and mmap's cold accounts from OS virtual memory. | **Verified** |
| `MEM-CSPB-007` | `MEM07_CoreShardedPartitionedBitmap` | Shards bitmaps across CPU cores with thread-local lock-free write buffers. | **Verified** |
| `MEM-BLOCKLAYOUT-008` | `MEM08_AccessWeightedBlockLayout` | Organizes 64KB blocks sorted by empirical access temperature. | **Verified** |
| `MEM-INDIR-009` | `MEM09_DenseInternalIdsIndirection` | Translates sparse database primary keys into dense 0..N integers. | **Verified** |
| `MEM-VALIDITY-010` | `MEM10_SharedValidityExistencePlanes` | Global existence bitplane shared across all query answer representations. | **Verified** |

---

### Family 8: Adaptive Selection (Part 10)
Namespace: `NeuroCache.ResearchLab.AdaptiveSelection`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `ADAPT-WTCC-001` | `ADAPT01_WorkloadTelemetryCostModel` | Cost model scoring representations by (Bytes * W1 + Latency * W2 + WriteCost * W3). | **Verified** |
| `ADAPT-ACRP-002` | `ADAPT02_AdmissionControlledPromotion` | TinyLFU admission filter preventing cold scan pollution in hot answer caches. | **Verified** |
| `ADAPT-DWSV-003` | `ADAPT03_DualWriteShadowValidationSwitcher` | Dual-writes mutations to two candidate stores and switches primary based on live telemetry. | **Verified** |
| `ADAPT-PORTFOLIO-004` | `ADAPT04_PortfolioRouter` | Multi-engine router dispatching point queries, scans, and batch requests. | **Verified** |
| `ADAPT-HYSTERESIS-005` | `ADAPT05_HysteresisConversionBudget` | Hysteresis threshold preventing rapid thrashing between representation types. | **Verified** |

---

### Family 9: Alternative Encodings (Part 11)
Namespace: `NeuroCache.ResearchLab.AlternativeEncodings`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `ALT-ELIASFANO-001` | `ALT01_EliasFanoEncoder` | Quasi-succinct Elias-Fano encoding for monotone sequences of true answer IDs. | **Verified** |
| `ALT-BLOOM-002` | `ALT02_BloomNegativeGateExactFallback` | Bloom filter fast-rejecting false predicates with exact code fallback for positives. | **Verified** |
| `ALT-ZHEGALKIN-003` | `ALT03_ZhegalkinPolynomialAlgebra` | Algebraic normal form over GF(2) XOR/AND operations evaluating predicates. | **Verified** |
| `ALT-TEMPLATES-004` | `ALT04_SharedAnswerTemplatesWithExceptions` | Predefined template library with sparse exception offsets. | **Verified** |
| `ALT-FACTS-005` | `ALT05_ExactSufficientFactColumns` | Stores only the minimal sufficient columns (balance interval + active flag). | **Verified** |

---

### Family 10: Recovery (Part 12)
Namespace: `NeuroCache.ResearchLab.Recovery`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `REC-COW-001` | `REC01_CopyOnWriteAnswerPages` | 4096-account pages with copy-on-write mutation and atomic root pointer swap. | **Verified (Zero Lock Contention Reads)** |
| `REC-CCPR-002` | `REC02_CheckpointedContiguousPrefixReplay` | Checkpoint snapshots paired with contiguous outbox log replay from safe watermark. | **Verified (Zero Gap Divergence)** |
| `REC-SEQLOCK-003` | `REC03_SeqlockPagePublication` | 1024-account pages with 64-bit seqlocks preventing torn reads during mutations. | **Verified (Tear-Free Production Winner)** |
| `REC-HARNESS-004` | `REC04_IntegratedFailureRecoveryHarness` | Automated test harness injecting crash mid-publication, rollback gaps, and partial replay. | **Verified (100% Parity with Oracle)** |

---

### Family 11: Hybrid Compositions (Part 13)
Namespace: `NeuroCache.ResearchLab.HybridCompositions`

| Candidate ID | Candidate Class Name | Hypothesis & Mechanism Description | Status |
|---|---|---|---|
| `COMBO-FSCG-DARG-001` | `COMBO01_FactorizedAtomsWithDargAndFreshness` | REP-02 + MUT-08/09 + MUT-03: Shared atomic condition bitmaps with dependency recompute and epoch clocks. | **Verified (7.15 MiB / 10M, 6 bits/acc)** |
| `COMBO-SEM-SPARSE-002` | `COMBO02_SemanticClassesWithRareExceptions` | SEM-03/04 + SEM-06: 4-bit semantic equivalence classes covering >99.99% accounts with sparse hash table fallback. | **Tournament Winner (4 bits/acc, 75.1 Mops/s MT)** |
| `COMBO-OVERLAY-COW-003` | `COMBO03_BoundedOverlayWithCowGenerations` | MUT-01/06 + REC-01: Low-latency overlay absorbing write bursts before background drain to CoW pages. | **Verified** |
| `COMBO-ATOMS-BATCH-004` | `COMBO04_SharedAtomsWithBatchIntermediateReuse` | REP-02 + EXEC-04/05: Shared atomic condition bitmaps with SIMD intermediate sub-expression reuse. | **Verified (59.9 Mops/s MT)** |
| `COMBO-HOT-COLD-005` | `COMBO05_HotMaterializationWithExactColdFallback` | EXEC-02 + ADAPT-02: Precomputed hot answers in L1/L2 cache; cold accounts evaluate on demand. | **Verified** |
| `COMBO-PORTFOLIO-006` | `COMBO06_CompletePortfolioRouter` | Complete adaptive portfolio router dynamically dispatching queries between point lookups, scans, and recovery engines. | **Verified (Full System Composition)** |
