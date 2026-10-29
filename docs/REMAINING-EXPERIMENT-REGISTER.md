# NeuroCache ResearchLab — Definitive Remaining Experiment Register

**Repository:** `almas-codes/memory-systems-lab`  
**Execution Date:** October 29, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)  
**Target Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Machine-Readable Inventory:** [`research/NeuroCache.ResearchLab/Results/2026-10-29_Phase2_Advanced_Research/Raw/remaining-experiments-register.json`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/Results/2026-10-29_Phase2_Advanced_Research/Raw/remaining-experiments-register.json)  

---

## 1. Classification Methodology & Status Taxonomy

In accordance with Phase 2 integrity standards, no experiment is classified as complete merely because it compiles or passes trivial unit tests. Every candidate architecture in the research repository has been audited and assigned exactly one of the nine authoritative status categories:

| Status Code | Definition & Criteria | Count |
|:---|:---|:---:|
| `BENCHMARKED_COMPLETE` | Verified against exact oracle, passed multi-threaded concurrency/stress tests, and fully benchmarked across target workloads (10M/50M). | **25** |
| `REJECTED_WITH_EVIDENCE` | Implementable architecture empirically eliminated due to severe memory blowup, lock serialization, or cache thrashing. Preserved for scientific negative evidence. | **8** |
| `CORRECTNESS_PASSED_NOT_BENCHMARKED` | Concrete class implemented, compiled, and passed full unit correctness against `PureCodeOracle`, but not yet subjected to high-contention saturation tournaments. | **35** |
| `BUILT_NOT_TESTED` | Implementation scaffolded or designated for current Phase 2 execution tracks (Tracks 1 through 4). | **4** |
| `BLOCKED_WITH_REASON` | Execution blocked by real host hardware constraints (e.g. AVX-512 execution on AMD Zen 3 host). | **1** |
| `IMPLEMENTED_UNVERIFIED` | Class exists but has unexecuted test assertions. | **0** |
| `IMPLEMENTED_BUILD_FAILED` | Class fails compilation. | **0** |
| `CORRECTNESS_FAILED` | Fails exact oracle parity tests. | **0** |
| `BENCHMARKED_INCOMPLETE` | Partial benchmark results without latency percentiles. | **0** |

**Total Registered Candidates & Tracks:** **73 Candidates**

---

## 2. Comprehensive Candidate Inventory

### 2.1. Production Winner & Primary Finalists
| Candidate ID | Legacy / Astra ID | Class Name | Research Family | Status | Verification Summary |
|---|---|---|---|:---:|:---|
| `PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001` | `PROD-01` | `PROD01_FourBitExceptionSeqlock` | ProductionCandidate | **`BENCHMARKED_COMPLETE`** | 100% 400M oracle parity on 50M records; 14 concurrency gates passed; 53 ns p50 latency; 26.95 MB memory. |
| `SEM-CLASS4-003` | `SEM-03` | `SEM03_FourBitSemanticClasses` | SemanticEncoding | **`BENCHMARKED_COMPLETE`** | 37.85 Mops/s ST read, 21.0 ns p50, 182.4 Mops/s adjacent CAS, 25.00 MB memory. |
| `COMBO-SEM-SPARSE-002` | `COMBO-02` | `COMBO02_SemanticClassesWithRareExceptions` | HybridCompositions | **`BENCHMARKED_COMPLETE`** | 26.88 Mops/s ST read, 40.0 ns p50, 98.75 Mops/s Zipf, slot 0x0F open-world escape. |
| `SEM-BYTEVEC-002` | `SEM-02` | `SEM02_ByteAnswerVector` | SemanticEncoding | **`BENCHMARKED_COMPLETE`** | 38.10 Mops/s ST read, 218.95 Mops/s MT8, 50.00 MB memory. |
| `REP-CJB-001` | `REP-01` | `REP01_CorrelatedOutcomeJointBitmap` | Representations | **`BENCHMARKED_COMPLETE`** | 33.64 Mops/s ST read, 115.94 Mops/s MT16, 50.00 MB memory. |
| `SEM-BITMAPS-001` | `SEM-01` | `SEM01_IndependentBitmaps` | SemanticEncoding | **`BENCHMARKED_COMPLETE`** | Baseline: 10.74 Mops/s ST read, 105.78 Mops/s MT16, 50.00 MB memory. |
| `REC-SEQLOCK-003` | `REC-03` | `REC03_SeqlockPagePublication` | Recovery | **`BENCHMARKED_COMPLETE`** | 20.74 Mops/s ST read, 50.78 MB memory, zero torn reads across 11.6M writes. |

### 2.1B. Production Integrated Hybrid Tiered Engine
| Candidate ID | System Name | Key Components | Research Family | Status | Verification Summary |
|---|---|---|---|:---:|:---|
| `HYBRID-TIERED-ENGINE-001` | `HybridQueryRouter` | `PROD-01` + `THR-02` + CDC Coordinator | HybridTieredEngine | **`COMPLETED_VERIFIED`** | Unified engine for OLTP point lookups (14 ns p50) and analytical range scans (2.28 Giga-records/s); 18/18 integration tests pass; 6/6 crash recovery gates pass; 18.25 MB / 10M records. |
| `HYBRID-OPT-01` | `SharedConditionReuser` | `SourceConditionToken` pre-decode | HybridOptimizations | **`COMPLETED_VERIFIED`** | Single decode feeds both point and analytical engines; zero redundant evaluations. |
| `HYBRID-OPT-02` | `BatchAwareRouter` | Vectorized batch selector | HybridOptimizations | **`COMPLETED_VERIFIED`** | Break-even measured at batch size $N \ge 16$. |
| `HYBRID-OPT-03` | `AdaptiveWorkloadRouter` | Hysteresis telemetry controller | HybridOptimizations | **`COMPLETED_VERIFIED`** | 60% analytical trigger; recommendation mode verified without route flapping. |
| `HYBRID-OPT-04` | `ConcurrentSchemaEvolutionEngine` | Online 8-bit promotion coordinator | HybridOptimizations | **`COMPLETED_VERIFIED`** | Coordinates live dictionary swap while traffic seamlessly uses fallback. |

### 2.2. Validated Structural & Algorithmic Baselines
| Candidate ID | Legacy / Astra ID | Class Name | Research Family | Status | Verification Summary |
|---|---|---|---|:---:|:---|
| `REP-FSCG-002` | `REP-02` | `REP02_FactorizedSharedConditionGraph` | Representations | **`BENCHMARKED_COMPLETE`** | 7.15 MiB / 10M (6.0 bits/acc), 36.41 Mops/s MT read. |
| `COMBO-ATOMS-BATCH-004` | `COMBO-04` | `COMBO04_SharedAtomsWithBatchIntermediateReuse` | HybridCompositions | **`BENCHMARKED_COMPLETE`** | 7.15 MiB / 10M, 59.90 Mops/s MT read. |
| `COMBO-FSCG-DARG-001` | `COMBO-01` | `COMBO01_FactorizedAtomsWithDargAndFreshness` | HybridCompositions | **`BENCHMARKED_COMPLETE`** | 7.15 MiB / 10M, 32.86 Mops/s MT read. |
| `REC-CCPR-002` | `REC-02` | `REC02_CheckpointedContiguousPrefixReplay` | Recovery | **`BENCHMARKED_COMPLETE`** | 20.0 MB / 10M, 92.83 Mops/s MT read, rollback gap safe. |
| `COMBO-PORTFOLIO-006` | `COMBO-06` | `COMBO06_CompletePortfolioRouter` | HybridCompositions | **`BENCHMARKED_COMPLETE`** | 25.0 MB / 10M, 64.70 Mops/s MT read. |
| `THR-BITSLICE-002` | `THR-02` | `THR02_BitSlicedNumericEvaluation` | ThresholdEncoding | **`BENCHMARKED_COMPLETE`** | Bit-sliced columnar integers, 64 accounts simultaneously. |
| `THR-DIR-001` | `THR-01` | `THR01_ThresholdDecisionDirectory` | ThresholdEncoding | **`BENCHMARKED_COMPLETE`** | 6 interval partitions across 5 balance thresholds. |
| `THR-COMPARE-003` | `THR-03` | `THR03_IntervalVsBitSlicedComparator` | ThresholdEncoding | **`BENCHMARKED_COMPLETE`** | Comparator harness screening directory vs bit-sliced execution. |
| `SEM-DICT-005` | `SEM-05` | `SEM05_DictionaryEncodedAnswerVectors` | SemanticEncoding | **`BENCHMARKED_COMPLETE`** | Global frequency-sorted dictionary. |
| `SEM-SPARSE-006` | `SEM-06` | `SEM06_SparseRareClassExceptions` | SemanticEncoding | **`BENCHMARKED_COMPLETE`** | Dense array + concurrent hash table fallback. |
| `SEM-BLOCKDICT-007` | `SEM-07` | `SEM07_BlockLocalSemanticDictionaries` | SemanticEncoding | **`BENCHMARKED_COMPLETE`** | 64KB block-local dictionaries. |
| `SEM-PROMOT-008` | `SEM-08` | `SEM08_AdaptiveClassWidthPromotion` | SemanticEncoding | **`BENCHMARKED_COMPLETE`** | 3-bit to 4-bit automatic block promotion. |
| `FRESH-GECI-003` | `MUT-03` | `MUT03_GlobalEpochClockInvalidation` | FreshnessAndPublication | **`BENCHMARKED_COMPLETE`** | 64-bit monotonic epoch clock eliminating wraparound. |
| `FRESH-DCV-007` | `MUT-07` | `MUT07_DependencyClockVector` | FreshnessAndPublication | **`BENCHMARKED_COMPLETE`** | Partition-level causal dependency clock vector. |
| `MUT-CATC-004` | `MUT-04` | `MUT04_ChangeAwareTransitionCompression` | MutationMaintenance | **`BENCHMARKED_COMPLETE`** | Bitwise transition filter suppressing redundant mutations. |
| `MUT-DARG-008` | `MUT-08` | `MUT08_DependencyAwareRecomputeGraph` | MutationMaintenance | **`BENCHMARKED_COMPLETE`** | Field-to-predicate recompute graph. |
| `MUT-ROUTER-009` | `MUT-09` | `MUT09_FieldAddressableChangeRouting` | MutationMaintenance | **`BENCHMARKED_COMPLETE`** | Column bitmask mutation dispatcher. |
| `REC-HARNESS-004` | `REC-04` | `REC04_IntegratedFailureRecoveryHarness` | Recovery | **`BENCHMARKED_COMPLETE`** | Mid-publication crash and gap recovery validation harness. |

### 2.3. Formally Rejected Architectures (Preserved Negative Results)
| Candidate ID | Legacy ID | Class Name | Status | Empirical Failure Mode |
|---|---|---|:---:|:---|
| `SEM-CLASS3-004` | `SEM-04` | `SEM04_ThreeBitSemanticClasses` | **`REJECTED_WITH_EVIDENCE`** | Bit-straddling byte boundaries causes 13x write slowdown (3.51 Mops/s) and slot exhaustion at 8 classes. |
| `COMBO-HOT-COLD-005` | `COMBO-05` | `COMBO05_HotMaterializationWithExactColdFallback` | **`REJECTED_WITH_EVIDENCE`** | 320 MB memory on 10M accounts (32x blowup vs 10 MB flat array) due to hash node pointer overhead. |
| `EXEC-HHCM-002` | `EXEC-02` | `EXEC02_HybridHotColdMaterialization` | **`REJECTED_WITH_EVIDENCE`** | Hash collisions and pointer misses under access pattern drift. |
| `COMBO-OVERLAY-COW-003` | `COMBO-03` | `COMBO03_BoundedOverlayWithCowGenerations` | **`REJECTED_WITH_EVIDENCE`** | 291 MB memory and 0.40 Mops/s write speed during burst writes. |
| `REC-COW-001` | `REC-01` | `REC01_CopyOnWriteAnswerPages` | **`REJECTED_WITH_EVIDENCE`** | 4KB allocations per write trigger severe GC Gen 0/1 pauses and CAS spin thrashing. |
| `EXEC-ZSPC-001` | `EXEC-01` | `EXEC01_ZeroStoragePredicateCompiler` | **`REJECTED_WITH_EVIDENCE`** | 192 ns latency, 5.2 Mops/s throughput (16x drop vs cache). |
| `MEM-ROARING-001` | `MEM-01` | `MEM01_RoamingSparseDenseBitmap` | **`REJECTED_WITH_EVIDENCE`** | In dense contiguous accounts (1..50M), chunk directories add 0.2 MB overhead with zero compression benefit. |
| `ALT-BLOOM-002` | `ALT-02` | `ALT02_BloomNegativeGateExactFallback` | **`REJECTED_WITH_EVIDENCE`** | 39% false positive rate requires full DB rechecks, destroying p99 tail latency. |

### 2.4. Phase 2 Research Tracks
| Candidate ID | Track ID | Class Name | Research Family | Status | Target Objective |
|---|---|---|---|:---:|:---|
| `SCHEMA-ONLINE-REPARTITION-001` | Track 1 | `SchemaOnlineRepartitionRunner` | DynamicSchema | **`BUILT_NOT_TESTED`** | Online registration of 9th/10th predicates with background generation rebuilding and atomic pointer swap. |
| `SIMD-BITSLICE-001` | Track 2 | `SimdBitSlicedThresholdRunner` | SimdAcceleration | **`BUILT_NOT_TESTED`** | Direct AVX2 SIMD threshold scans and bit-sliced arithmetic with safe runtime fallback. |
| `SCALE-500M-001` | Track 3 | `Scale500MRunner` | ScaleEnvelope | **`BUILT_NOT_TESTED`** | Synthetic memory scaling from 50M to 500M records (250 MB raw payload, 269.5 MB static). |
| `REPL-CDC-001` | Track 4 | `DistributedCdcReplicationRunner` | DistributedReplication | **`BUILT_NOT_TESTED`** | Multi-replica local seqlock publication from PostgreSQL outbox stream. |
