# NeuroCache ResearchLab — Master Documentation & Research Index

**Repository:** `almas-codes/memory-systems-lab`  
**Latest Authoritative Report:** [**`docs/NEXT-RESEARCH-DECISION.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/NEXT-RESEARCH-DECISION.md)  
**Execution Date:** October 30, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)  
**Production Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Host Platform:** AMD Ryzen 7 5825U (8 Physical Cores, 16 Logical Processors, 16 MB L3 Cache, Windows 11 x64, .NET 10.0.100, PostgreSQL 17.2)  
**Production Status:** **`Production readiness supported for the explicitly tested scope` (`HYBRID-TIERED-ENGINE-001`)**

---

## 1. Quick Navigation to Authoritative Hybrid Tiered Engine Reports

| Document Path | Description & Scope | Key Empirical Takeaway |
|---|---|---|
| [**`docs/NEXT-RESEARCH-DECISION.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/NEXT-RESEARCH-DECISION.md) | **Master Final Synthesis & Research Decision.** Comprehensive report answering all 10 explicit research questions from empirical data. | Definitive answers to all 10 research questions; formally supports controlled production pilot. |
| [**`docs/HYBRID-ARCHITECTURE.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/HYBRID-ARCHITECTURE.md) | Architectural contract, component responsibilities, data ownership, routing invariants, and consistency boundaries. | Unifies `PROD-01` (OLTP point) and `THR-02` (analytics) under deterministic `HybridQueryRouter`. |
| [**`docs/HYBRID-EVIDENCE-RECONCILIATION.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/HYBRID-EVIDENCE-RECONCILIATION.md) | Mathematical continuous domain derivation (9 actual vectors, $\le 15$ continuous), common 10M/50M analytical reconciliation, and timer quantization. | Explains earlier 10M vs 50M PostgreSQL discrepancy; verifies 2.28 Giga-records/s scalar scan rate. |
| [**`docs/HYBRID-CONSISTENCY-CONTRACT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/HYBRID-CONSISTENCY-CONTRACT.md) | Freshness guarantees: Mode A (SourceCurrent), Mode B (ReadYourWrites), Mode C (BoundedStaleness). | Zero torn reads across 50k reads during 500 class transitions; monotonic causal version gating. |
| [**`docs/HYBRID-IMPLEMENTATION-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/HYBRID-IMPLEMENTATION-REPORT.md) | Complete source code inventory and implementation review of contracts, engines, coordinators, and router. | Full codebase documentation of `NeuroCache.ResearchLab.HybridEngine` namespace. |
| [**`docs/HYBRID-INTEGRATION-CORRECTNESS.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/HYBRID-INTEGRATION-CORRECTNESS.md) | Comprehensive scorecard of all 18 end-to-end integration tests through the public router. | **18 / 18 Tests Passed (100.0%)**. Point lookups, analytical scans, timeouts, out-of-order CDC, rebuilds. |
| [**`docs/HYBRID-RECOVERY-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/HYBRID-RECOVERY-REPORT.md) | Deterministic crash recovery audit evaluating 6 distinct crash points between commit and publication. | **6 / 6 Crash Gates Passed (100.0%)**. Clean replay from PostgreSQL outbox WAL; dual-engine convergence. |
| [**`docs/HYBRID-BENCHMARK-RESULTS.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/HYBRID-BENCHMARK-RESULTS.md) | Multi-workload comparison tournament across 10M records (Point-Heavy, Analytical-Heavy, Balanced, Zipfian, Mutations). | 561x speedup over PostgreSQL on point-heavy; 2,178x on analytical; 6.36 Mops/s concurrent mutation rate. |
| [**`docs/HYBRID-PRODUCTION-READINESS.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/HYBRID-PRODUCTION-READINESS.md) | 12-gate production readiness scorecard, operational constraints, and formal recommendation. | **12 / 12 Production Gates Passed (100.0%)**. Approved for controlled production pilot. |
| [**`docs/REMAINING-EXPERIMENT-REGISTER.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/REMAINING-EXPERIMENT-REGISTER.md) | Definitive catalog of all 73 laboratory candidates plus Hybrid Tiered Engine entries with 9 strict status codes. | Updated with `HYBRID-TIERED-ENGINE-001` and `HYBRID-OPT-01..04`. |

---

## 2. Phase 2 Advanced Research Track Reports

| Document Path | Description & Scope |
|---|---|
| [**`docs/PRODUCTION-CANDIDATE-REVALIDATION.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/PRODUCTION-CANDIDATE-REVALIDATION.md) | Revalidation of `PROD-01`, mathematical continuous domain bound, and unquantized latency. |
| [**`docs/ONLINE-QUERY-REPARTITION-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/ONLINE-QUERY-REPARTITION-REPORT.md) | Dynamic schema evolution engine evaluating Variant A (Atomic Swap), Variant B (Dual-Write), Variant C (Lazy Page Migration), and 8-bit promotion (`SEM-08`). |
| [**`docs/SIMD-THRESHOLD-EXPERIMENTS.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/SIMD-THRESHOLD-EXPERIMENTS.md) | SIMD acceleration and bit-sliced numeric evaluation (`THR-02`) on AMD Zen 3 host. |
| [**`docs/SCALE-500M-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/SCALE-500M-REPORT.md) | Memory and latency scaling up to 500,000,000 records; OS page locking audit. |
| [**`docs/DISTRIBUTED-CDC-REPLICATION-REPORT.md`**](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/docs/DISTRIBUTED-CDC-REPLICATION-REPORT.md) | Multi-node Write-Ahead Log CDC replication with local seqlock read isolation on replicas. |

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

# 3. Execute Complete Hybrid Tiered Engine Suite (Reconciliation + Integration + Benchmarks)
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --hybrid-all

# Or execute individual Hybrid Tiered Engine suites:
# Phase Zero: Evidence Reconciliation & Benchmark Audit
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --hybrid-reconcile

# Phase Six: 18 Hybrid Integration Tests & 6 Crash Recovery Gates
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --hybrid-integration

# Phase Seven: Full Hybrid System Benchmark Tournament (10M / 50M)
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --hybrid-benchmark

# 4. Phase 2 Advanced Tracks:
dotnet run -c Release --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj --no-build -- --phase2-all
```
