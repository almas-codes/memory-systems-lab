# NeuroCache ResearchLab — Hybrid Tiered Engine Architecture

**Repository:** `almas-codes/memory-systems-lab`  
**Execution Date:** October 30, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)  
**Core Components:** `PROD-01`, `THR-02`, PostgreSQL Outbox CDC, and Local Seqlock Publication  
**Document Purpose:** Architectural contract, component responsibilities, data ownership, routing invariants, and consistency boundaries.

---

## 1. System Overview & Architectural Motivation

Previous research in the NeuroCache ResearchLab proved two complementary, non-overlapping performance peaks:
1. **`PROD-01` (4-Bit Semantic Class with Seqlock Pages):** Unmatched for OLTP point lookups ($>85\text{ Mops/s}$, $53\text{ ns}$ p50 latency, $4.31\text{ bits/account}$ at 50M records, zero torn reads).
2. **`THR-02` (Bit-Sliced Numeric Evaluation):** Unmatched for analytical range scans and cohort counts ($>2,000\text{ Mrec/s}$, $0.48\text{ ns/account}$, 194x faster than PostgreSQL parallel scans).

However, in real banking and fintech systems, workloads are mixed: high-frequency transaction verification requires single-account point queries, while risk analysis, marketing campaigns, and AML screening require bulk range scans.

The **Hybrid Tiered Engine** unifies these two engines behind a deterministic, type-classified routing contract while preserving exact authoritative fallback to PostgreSQL.

```
+----------------------------------------------------------------------------------------------------+
|                                NEUROCACHE HYBRID TIERED ENGINE ARCHITECTURE                         |
+----------------------------------------------------------------------------------------------------+
|                                                                                                    |
|                               +-------------------------------+                                    |
|                               |     PostgreSQL 17.2 Master    |                                    |
|                               |   (Authoritative Single Truth)|                                    |
|                               +-------------------------------+                                    |
|                                               |                                                    |
|                                    Monotonic Outbox WAL CDC                                        |
|                                               |                                                    |
|                                               v                                                    |
|                               +-------------------------------+                                    |
|                               |   SourceVersionCoordinator    |                                    |
|                               |  (CommitSeq, Watermark, Wait) |                                    |
|                               +-------------------------------+                                    |
|                                               |                                                    |
|                                               v                                                    |
|                               +-------------------------------+                                    |
|                               |    IRepresentationPublisher   |                                    |
|                               |  (Variant A, B, or C Protocol)|                                    |
|                               +-------------------------------+                                    |
|                                       /               \                                            |
|                        Sync/Async    /                 \   Sync/Async                              |
|                       Publication   /                   \  Publication                             |
|                                    v                     v                                         |
|              +--------------------------+   +--------------------------+                           |
|              |     Prod01PointEngine    |   |   Thr02AnalyticalEngine  |                           |
|              | (4-bit nibbles, Seqlock) |   | (10 vertical bit-planes) |                           |
|              |  - OLTP Point Lookups    |   |  - Analytical Range Scan |                           |
|              |  - 0x0F Sparse Exception |   |  - Batch Multi-Threshold |                           |
|              +--------------------------+   +--------------------------+                           |
|                           ^                              ^                                         |
|              Point Read   |                              | Analytical Read                         |
|                           +--------------+---------------+                                         |
|                                          |                                                         |
|                               +----------------------+                                             |
|                               |  HybridQueryRouter   | <====== [Client QueryRequests]              |
|                               |  (Classification &  |                                              |
|                               |   Freshness Gate)    | === fallback ===> [PostgreSqlExactFallback] |
|                               +----------------------+                                             |
|                                                                                                    |
+----------------------------------------------------------------------------------------------------+
```

---

## 2. Component Responsibilities & Data Ownership

| Component | Interface | Concrete Implementation | State & Data Ownership | Primary Responsibility |
|---|---|---|---|---|
| **Point Query Tier** | `IPointQueryEngine` | [`Prod01PointEngine`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/HybridEngine/Engines/Prod01PointEngine.cs) | 4-bit nibble array `_data`, 16-slot dictionary `_classToPattern`, sparse exception map `_sparseExceptions`, 1024-account page seqlocks. | Evaluates single-account point queries (Q1..Q8), extracts 8-bit answer vectors, guarantees tear-free point reads under concurrent writes. |
| **Analytical Scan Tier** | `IAnalyticalQueryEngine` | [`Thr02AnalyticalEngine`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/HybridEngine/Engines/Thr02AnalyticalEngine.cs) | 10 vertical 64-bit bit-planes `_planes[0..9]`, packed active bitmap `_activeBits`. | Evaluates bulk range counts (`balance >= threshold`), multi-threshold batches, matching ID extraction, using 64-account parallel bitwise logic. |
| **Authoritative Fallback** | `IExactFallback` | [`PostgreSqlExactFallback`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/HybridEngine/Engines/PostgreSqlExactFallback.cs) | PostgreSQL connection pool and in-memory fallback ground truth. | Executes queries when predicates are unsupported, freshness timeouts expire, or schema migrations are in progress. |
| **Source Coordinator** | `ISourceVersionCoordinator` | [`SourceVersionCoordinator`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/HybridEngine/Coordination/SourceVersionCoordinator.cs) | Monotonic `_currentSourceVersion`, `_publishedWatermark`, `_versionWaiters` map. | Allocates transaction commit sequences, orders CDC events, wakes up threads waiting for specific commit versions. |
| **Representation Publisher** | `IRepresentationPublisher` | [`SerializedPublicationCoordinator`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/HybridEngine/Coordination/PublicationVariants.cs) (Var A), `Generational` (Var B), `PerDomain` (Var C) | Published version metadata per engine. | Dispatches incoming mutations to point and analytical engines, advances published versions atomically. |
| **Query Router** | `IHybridQueryRouter` | [`HybridQueryRouter`](file:///d:/Projects/ProjectCache/NeuroCache.ResearchLab/research/NeuroCache.ResearchLab/HybridEngine/Routing/HybridQueryRouter.cs) | Diagnostics counters `HybridDiagnostics`, migration flag `_isMigrating`. | Classifies requests, enforces consistency modes, routes to optimal engine, dispatches fallback. |

---

## 3. Query Classification & Routing Contract

The query router uses typed request metadata (`QueryRequest`) to prevent brittle string SQL parsing:

```
                                  Incoming QueryRequest
                                            │
                                ┌───────────┴───────────┐
                                │ In Schema Migration?  │
                                └───────────┬───────────┘
                                       YES  │  NO
                       ┌────────────────────┘  └─────────────────────┐
                       ▼                                             ▼
            [Exact Fallback to PG]                       Classify Request Type
                                                                     │
                   ┌───────────────────────────────┬─────────────────┴─────────────────┐
                   ▼                               ▼                                   ▼
        [RequestType.PointPredicate]    [RequestType.AnalyticalCount/Batch]    [Unsupported / CustomSql]
                   │                               │                                   │
         Supports Q0..Q7?               Threshold < 1024?                              │
            YES │  NO                      YES │  NO                                   │
       ┌────────┘  └────────┐         ┌────────┘  └────────┐                           │
       ▼                    ▼         ▼                    ▼                           ▼
 Freshness OK?           [Fallback] Freshness OK?       [Fallback]            [Exact Fallback to PG]
   YES │  NO                          YES │  NO
 ┌─────┘  └─────┐                   ┌─────┘  └─────┐
 ▼              ▼                   ▼              ▼
[PROD-01 Point] [Wait/Fallback]    [THR-02 Scan]   [Wait/Fallback]
```

### 3.1 Point Query Path (`PROD-01`)
- **Criteria:** `RequestType.PointPredicate` or `RequestType.PointAll`, `PredicateIndex` $\in [0, 7]$, `AccountId` $\le \text{MaxId}$.
- **Execution:** Reads 1024-account page seqlock pre-sequence counter, extracts 4-bit nibble from `_data[id >> 1]`, decodes dictionary pattern or reads sparse exception table, validates post-sequence counter.
- **Latency:** $\sim 8\text{ to }14\text{ ns}$ p50; zero locks on read path.

### 3.2 Analytical Query Path (`THR-02`)
- **Criteria:** `RequestType.AnalyticalCount`, `AnalyticalIds`, or `AnalyticalBatch`, `Threshold` $< 1024$.
- **Execution:** Iterates over 64-bit words across relevant bit-planes, evaluates threshold mask in bitwise logic, conjunctions with `_activeBits[w]`, sums via `BitOperations.PopCount`.
- **Throughput:** $>2,000\text{ Mrec/s}$ ($0.48\text{ ns/account}$).

### 3.3 Unsupported & Stale Fallback Path (`PostgreSQL`)
- **Criteria:** Custom SQL queries, predicates outside declared family ($P \ge 8$), thresholds $\ge 1024$, or queries requesting version $V > V_{\text{applied}}$ that exceed the freshness timeout budget.
- **Execution:** Dispatched asynchronously to PostgreSQL authoritative tables or in-memory oracle reference.

---

## 4. Publication Protocols & Concurrency Invariants

### 4.1 Variant A: Serialized Publication (Default)
Both engines are updated synchronously within a single publication lock before advancing `_publishedVersion`.
- **Invariant:** $V_{\text{point}} == V_{\text{analytical}} == V_{\text{published}}$ at all quiescent points.
- **Trade-off:** Minimal routing overhead; callers never observe version skew between point and analytical representations.

### 4.2 Variant B: Generational Publication
Updates are accumulated into a staging structure, followed by an atomic reference exchange of the generation token.
- **Invariant:** Readers always access an immutable generation snapshot.
- **Trade-off:** Completely eliminates writer-reader interference, but introduces brief GC allocation during generation swaps.

### 4.3 Variant C: Per-Domain Readiness
Tracks $V_{\text{point}}$ and $V_{\text{analytical}}$ independently.
- **Invariant:** A fast point query requiring version $V$ can execute immediately even if a heavy analytical backfill is currently processing version $V-1$.
- **Trade-off:** Maximizes point-query concurrency, but router must check domain-specific version readiness flags.

---

## 5. Memory Budget & Footprint

At 10,000,000 accounts:
- `PROD-01`: 5.14 MB (5.00 MB payload + 0.14 MB seqlocks).
- `THR-02`: 13.11 MB (11.92 MB 10 bit-planes + 1.19 MB active bitmap).
- Combined Hybrid Engine: **18.25 MB** (strictly **15.31 bits/account**).

At 50,000,000 accounts:
- `PROD-01`: 26.95 MB.
- `THR-02`: 65.57 MB (59.60 MB planes + 5.96 MB active bits).
- Combined Hybrid Engine: **92.52 MB** (strictly **15.52 bits/account**).
- Entire 50M hybrid cache consumes only **0.58% of a 16 GB server's RAM**!
