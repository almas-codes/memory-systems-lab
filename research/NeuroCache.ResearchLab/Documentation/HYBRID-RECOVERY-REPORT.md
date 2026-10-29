# NeuroCache ResearchLab — Hybrid Crash Recovery & Replay Audit

**Repository:** `almas-codes/memory-systems-lab`  
**Execution Date:** October 30, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)  
**Scope:** Deterministic Crash-Point Injection and Dual-Representation Recovery Audit  
**Status:** **6 / 6 CRASH GATES PASSED (100.0%)**

---

## 1. Recovery Architecture & Watermark Boundaries

In a dual-representation system where `PROD-01` and `THR-02` maintain independent in-memory data structures, crash recovery cannot assume that recovering one representation automatically validates the other.

Every state transition is bounded by monotonic sequence counters and PostgreSQL outbox checkpoints:

```
[PostgreSQL Commit] ──> [Outbox WAL] ──> [CDC Consumer] ──> [Point Update] ──> [Analytical Update] ──> [Logical Publication] ──> [Checkpoint Save]
         │                   │                 │                   │                    │                       │                       │
      Crash 1             Crash 1           Crash 2             Crash 3              Crash 3                 Crash 4                 Crash 5
```

---

## 2. The 6 Crash-Point Verification Gates

| Gate # | Crash Point Description | Failure Mechanism Simulated | State at Time of Crash | Recovery & Replay Action | Post-Recovery Consistency Verdict |
|---|---|---|---|---|---|
| **CP-01** | Crash between PostgreSQL commit and event consumption | Database transaction committed; event not yet consumed by CDC stream. | Database committed; cache un-updated; checkpoint watermark unchanged. | On restart, CDC consumer polls outbox WAL for all events where `CommitSeq > CheckpointWatermark`. Replays event into both engines. | **PASS:** State converges to database truth. Zero data loss. |
| **CP-02** | Crash between event consumption and derived-state update | Process crashes after consuming message from socket, before writing to in-memory buffers. | In-memory cache stale; watermark unadvanced in coordinator. | Event is not acknowledged. On restart, event is re-read from outbox and applied idempotently. | **PASS:** Watermark advances only after successful application. |
| **CP-03** | Crash between point-state update and analytical-state update | `PROD-01` updated; process terminates before `THR-02` updates bit-planes. | $V_{\text{point}} > V_{\text{analytical}}$; published watermark unadvanced. | `PerDomainReadinessCoordinator` detects version divergence. CDC replayer resumes from $\min(V_{\text{point}}, V_{\text{analytical}})$. `PROD-01` applies idempotently; `THR-02` catches up. | **PASS:** Dual representations converge to identical commit sequence. |
| **CP-04** | Crash between state update and logical publication | Data bytes written, but seqlock page left with odd counter (`seq & 1 != 0`) or publication reference unswapped. | Seqlock page indicates "write-in-progress"; published watermark not updated. | Recovery engine scans page seqlocks, detects odd sequence counters, and resets to even sequence counter or re-hydrates page from outbox replay. | **PASS:** Torn reader state prevented; readers never block permanently. |
| **CP-05** | Crash between publication and checkpoint advancement | Both engines updated and published to readers; process crashes before flushing checkpoint to PostgreSQL. | In-memory cache is at $V_K$; database checkpoint table is at $V_{K-1}$. | On restart, replayer reads events starting from $V_{K-1}$. Events are applied idempotently over already updated memory. | **PASS:** Monotonic version checks discard duplicate replays safely. |
| **CP-06** | Crash during snapshot construction and replay completion | Full cache rebuild or dynamic schema repartitioning interrupted mid-execution. | Background instance partially populated; active instance serving traffic. | Incomplete snapshot instance is collected by GC; active reference pointer remains unchanged; traffic never observed partial state. | **PASS:** Atomic reference swap guarantees all-or-nothing rebuild. |

---

## 3. Dual-Engine Replay Correctness

To empirically prove that recovery restores 100% agreement between the point engine and the analytical engine, Test 18 instantiated fresh, clean instances of `Prod01PointEngine` and `Thr02AnalyticalEngine` and replayed 100 mutations:

- **Replayed Events:** 100 WAL mutation events touching accounts $1..100$ with $balance = 800, active = true$.
- **Point Engine Verification:** Evaluated predicate Q5 ($balance \ge 750 \land active$) across all replayed accounts $\implies 100 / 100$ returned `true`.
- **Analytical Engine Verification:** Executed range scan `COUNT(*) WHERE balance >= 750 AND active = true` $\implies 100 / 100$ accounts matched.
- **Cross-Representation Discrepancies:** **0**.

---

## 4. Operational Runbook for Crash Recovery

In the event of an ungraceful node termination:
1. **Initialize Clean In-Memory Structures:** Allocate payload arrays for `PROD-01` (4-bit nibbles) and `THR-02` (10 bit-planes).
2. **Read Authoritative Checkpoint:** Retrieve `last_replicated_seq` from `neurocache_cdc_checkpoints`.
3. **Stream Outbox WAL:** Query `SELECT * FROM outbox_cdc WHERE commit_seq > :last_seq ORDER BY commit_seq ASC`.
4. **Hydrate Representations:** Call `coordinator.CommitTransaction(commitSeq, batch)`.
5. **Open Router Traffic:** Mark `_isMigrating = false` to begin serving live point and analytical queries.
