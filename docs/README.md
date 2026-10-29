# NeuroCache ResearchLab — Master Documentation & Research Index

**Repository:** `almas-codes/memory-systems-lab`  
**Latest Authoritative Report:** [**`docs/NEXT-RESEARCH-DECISION.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/NEXT-RESEARCH-DECISION.md)  
**Execution Date:** October 29, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)  
**Production Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Host Platform:** AMD Ryzen 7 5825U (8 Physical Cores, 16 Logical Processors, 16 MB L3 Cache, Windows 11 x64, .NET 10.0.100, PostgreSQL 17.2)  
**Production Status:** **`Production readiness supported for the explicitly tested scope` (`PROD-01` / `PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001`)**

---

## 1. Quick Navigation to Authoritative Phase 2 Research Reports

| Document Path | Description & Scope | Key Empirical Takeaway |
|---|---|---|
| [**`docs/NEXT-RESEARCH-DECISION.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/NEXT-RESEARCH-DECISION.md) | **Master Final Synthesis & Research Decision.** Comprehensive report answering all 8 explicit Phase 2 research questions with empirical evidence and final architectural recommendation. | Answers 8 core research questions; establishes Hybrid Tiered Engine (`PROD-01` OLTP + `THR-02` Analytics + `REPL-CDC-001` Replicas). |
| [**`docs/PRODUCTION-CANDIDATE-REVALIDATION.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/PRODUCTION-CANDIDATE-REVALIDATION.md) | Revalidation of `PROD-01`, mathematical proof of 14-class continuous domain bound, exact 34.2 ns seqlock overhead, unquantized latency percentiles, and production label. | Continuous domain $\mathbb{R} \times \{0, 1\}$ has $\le 14$ distinct answer vectors $\implies$ 100% fit in 15 dense nibble slots with 0% sparse overflow. |
| [**`docs/ONLINE-QUERY-REPARTITION-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/ONLINE-QUERY-REPARTITION-REPORT.md) | **Track 1:** Dynamic schema evolution engine evaluating Variant A (Atomic Swap), Variant B (Dual-Write), Variant C (Lazy Page Migration), and 8-bit promotion (`SEM-08`). | 10/10 scenarios passed (100%). Zero torn reads across 100k+ concurrent reads during 10 atomic swaps; zero lost updates across 10k+ concurrent writes. |
| [**`docs/SIMD-THRESHOLD-EXPERIMENTS.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/SIMD-THRESHOLD-EXPERIMENTS.md) | **Track 2:** SIMD acceleration and bit-sliced numeric evaluation (`THR-02`) on AMD Zen 3 host. | Bit-Sliced Scalar achieves **2,067.65 Mrec/s** (0.48 ns/rec), 13.3x faster than direct column scans and 194.8x faster than PostgreSQL 17.2. AVX-512 marked `BLOCKED_HARDWARE`. |
| [**`docs/SCALE-500M-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/SCALE-500M-REPORT.md) | **Track 3:** Memory and latency scaling from 50M up to 500,000,000 records; OS page locking (`VirtualLock`) audit. | 500M static footprint bounded strictly at **269.53 MB** (4.31 bits/account). Point read latency shifts from 53 ns to 858 ns due to L3 miss / DRAM bus wait; scan throughput steady at 115.5 Mops/s. |
| [**`docs/DISTRIBUTED-CDC-REPLICATION-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/DISTRIBUTED-CDC-REPLICATION-REPORT.md) | **Track 4:** Multi-node Write-Ahead Log CDC replication with local seqlock read isolation on replicas. | Strong eventual consistency, causal ordering, 2.4M events/sec catchup throughput, horizontal read scaling to **112.4 Mops/s** across 8 replicas with < 0.25 ms lag. |
| [**`docs/REMAINING-EXPERIMENT-REGISTER.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/REMAINING-EXPERIMENT-REGISTER.md) | Exhaustive register tracking all 73 laboratory candidate technologies and hypotheses with 9 strict status codes. | Definitive tracking of all 73 candidates. Accompanied by machine-readable JSON [`remaining-experiments-register.json`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/Results/2026-10-29_Phase2_Advanced_Research/Raw/remaining-experiments-register.json). |
| [**`docs/REMAINING-EXPERIMENT-COMPLETION-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/REMAINING-EXPERIMENT-COMPLETION-REPORT.md) | Detailed disposition and technical rationale for all remaining backlog items. | Full audit of completed, rejected, blocked, and superseded architectures. |

---

## 2. Core Foundation & Phase 1 Deliverables

| Document Path | Description & Scope |
|---|---|
| [**`docs/PRODUCTION-GATE-DECISION.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/PRODUCTION-GATE-DECISION.md) | Master 10-gate audit of `PROD-01` on 50M records with 400M oracle comparisons. |
| [**`docs/FINAL-CANDIDATE-IMPLEMENTATION-REVIEW.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/FINAL-CANDIDATE-IMPLEMENTATION-REVIEW.md) | Code review of `PROD01_FourBitExceptionSeqlock.cs` data layout and seqlock synchronization. |
| [**`docs/EXHAUSTIVE-50M-PARITY-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/EXHAUSTIVE-50M-PARITY-REPORT.md) | 100% exhaustive streaming parity verification against PostgreSQL 17.2 (400M checks). |
| [**`docs/CONCURRENCY-AND-CLASS-GROWTH-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/CONCURRENCY-AND-CLASS-GROWTH-REPORT.md) | Stress testing under concurrent writes (11.6M same-record, 17.9M adjacent-record). |
| [**`docs/BENCHMARK-RECONCILIATION-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/BENCHMARK-RECONCILIATION-REPORT.md) | Timer quantum resolution and 50M performance tournament scorecard. |
| [**`docs/FINAL-CANDIDATE-MEMORY-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/FINAL-CANDIDATE-MEMORY-REPORT.md) | Detailed physical memory and cache residency breakdown. |
| [**`docs/TOURNAMENT-RESULTS.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/TOURNAMENT-RESULTS.md) | Multi-tier benchmark scorecard across 1M, 10M, and 50M records. |
| [**`docs/WINNER-ANALYSIS.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/WINNER-ANALYSIS.md) | In-depth trade-off analysis explaining why `PROD-01` was selected. |
| [**`docs/REJECTED-EXPERIMENTS.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/REJECTED-EXPERIMENTS.md) | Catalog of negative experimental results and architectural boundaries. |

---

## 3. Complete Reproduction Runbook

Every benchmark, test suite, and research experiment in the laboratory is 100% reproducible directly from the source code:

```powershell
# Navigate to the solution root
cd d:\Projects\ProjectCache\NeuroCache.ResearchLab

# 1. Clean build in Release mode (0 Warnings, 0 Errors)
dotnet clean -c Release NeuroCache.ResearchLab.slnx
dotnet build -c Release NeuroCache.ResearchLab.slnx

# 2. Run the complete automated test suite (94 unit/regression tests, 100% pass)
dotnet test -c Release NeuroCache.ResearchLab.slnx

# 3. Execute All Phase 2 Advanced Research Tracks (Tracks 1-4)
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --phase2-all

# Or execute individual research tracks:
# Track 1: Online Equivalence-Class Repartitioning
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --track1-repartition

# Track 2: SIMD Acceleration & Bit-Sliced Scans (includes PostgreSQL 17.2 comparison)
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --track2-simd

# Track 3: 500-Million-Record Scaling Envelope (50M, 100M, 250M, 500M)
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --track3-scale500m

# Track 4: Multi-Node Distributed CDC Replication & Local Seqlocks
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --track4-replication

# 4. Foundation / Phase 1 Benchmarks:
# 50M PostgreSQL Exhaustive Streaming Parity (400M checks)
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --parity-50m

# Class Growth and Seqlock Concurrency Gates
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --concurrency-audit

# 50M Benchmark Tournament
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --reconciliation-50m
```
