# NeuroCache ResearchLab — Final Rollback-Gap and Recovery-Liveness Validation

**Audit Date:** October 29, 2026  
**Repository:** `almas-codes/memory-systems-lab`  
**Starting Commit:** `608db3b`  
**Architecture:** PostgreSQL 17.2 Authoritative + EAP Boolean Bitmap & Packed Category Plane  
**Telemetry Location:** `results/rollback_gap_validation_20261029/`  

---

## Executive Summary & Core Decisions

This investigation experimentally validated the liveness, recovery, and memory properties of the NeuroCache EAP architecture under permanent sequence gaps (transaction rollbacks), delayed committed events, and projection failures.

```
========================================================================================================
 CORE QUESTION                         EMPIRICAL PROOF & FINDING                             VERDICT
========================================================================================================
 1. Watermark Advance After Rollback?   Advances 1 -> 3 -> 9 across single & 5 consecutive   CONFIRMED
                                       rollbacks. 0 stalls, 0 hangs.                        LIVENESS PRESERVED
 2. Delayed Committed Event Skipped?   Delayed seq 1 holds watermark at 0 while seq 2       CONFIRMED
                                       publishes. Recovery captured 100% committed events.  NEVER SKIPPED
 3. Recovery Converges to PostgreSQL?  Convergence verified across crashes, restart        CONFIRMED
                                       hydration (2.68 ms), and outbox reconciliation.      AUTHORITATIVE
 4. Safe & Bounded Metadata Pruning?   Pruned 1,000 entries with gaps to 0 steady-state (<5KB).CONFIRMED
                                       Corrected: 3.04 GB decimal = 2,903.5 MiB binary.     BOUNDED (6.25 MB)
                                       Category + Active Plane = 31.25 MB dec (29.8 MiB).
 5. Shadow Pilot Safe to Continue?     1,000-query stress run; circuit breaker tripped on   CONFIRMED
                                       lag=50; PG answered 100% of queries; 1.66 ms recov. PILOT SAFE
========================================================================================================
```

---

## 1. Permanent Sequence Gaps (Rollback Gaps) & Watermark Liveness

### 1.1 The Nature of Rollback Gaps in PostgreSQL
In PostgreSQL, `BIGSERIAL` / `nextval('seq')` sequence numbers are allocated outside transaction boundaries. When a transaction inserts into the outbox table and subsequently rolls back (due to application error, deadlock, serialization conflict, or constraint failure), that sequence number is **permanently burned** and will never appear in committed database storage.

A naive contiguous watermark tracker that expects sequence numbers $1, 2, 3, 4 \dots$ without gap tolerance would deadlock or stall indefinitely upon encountering a rolled-back sequence.

### 1.2 Liveness Verification (`rollback_gap_liveness_proof.json`)
We tested `ContiguousWatermarkTracker` and `EapCoordinator` against PostgreSQL-backed transactions with single and multiple consecutive rollbacks:

```
[Tx 1] Commit (AccountId: 1, Seq: 1)  ---> Watermark advances to 1
[Tx 2] Rollback (AccountId: 1, Seq: 2) ---> CancelInFlight(2) called; Watermark remains 1
[Tx 3] Commit (AccountId: 2, Seq: 3)  ---> AcknowledgePublication(3); Watermark cleanly advances to 3!
[5x Rollbacks] Raw PostgreSQL txns     ---> Seq 4, 5, 6, 7, 8 burned; Watermark remains 3
[Tx 9] Commit (AccountId: 3, Seq: 9)  ---> AcknowledgePublication(9); Watermark cleanly advances to 9!
```

#### Key Mechanics:
1. If a transaction encounters an error prior to commit, the coordinator's exception handler invokes `_watermarkTracker.CancelInFlight(seqId)`.
2. When the subsequent transaction commits and completes memory publication, `_inFlight.Count == 0`, and `_contiguousWatermark` advances to `_highestPublishedSeq`.
3. If an uncoordinated rollback occurs directly in PostgreSQL, the sequence number is never registered into `_inFlight`. When the next committed transaction publishes, the tracker advances past the gap immediately.
4. **Conclusion:** Watermark liveness is strictly preserved. Permanent rollback gaps never cause publication or recovery to stall.

---

## 2. Distinguishing Rollback Gaps from Delayed Committed Events

A critical architectural distinction exists between an aborted transaction and a delayed committed transaction:

| Dimension | Rollback Gap (Aborted Transaction) | Delayed Committed Event (In-Flight Publication) |
| :--- | :--- | :--- |
| **PostgreSQL Commit State** | **Not committed** (aborted) | **Committed** to PostgreSQL storage |
| **Outbox Presence** | Does **NOT** exist in `mutation_log` | **Exists** in `mutation_log` with valid payload |
| **`_inFlight` State** | Cancelled via `CancelInFlight(seq)` | Registered in `_inFlight` until publication completes |
| **Watermark Behavior** | Watermark advances past the missing number | Watermark is **held** at $\min(\text{inFlight}) - 1$ |
| **Crash Recovery Requirement**| Recovery must ignore the missing number | Recovery **must capture and replay** the outbox row |

### 2.1 Delayed Event Experiment (`delayed_vs_rollback_gap_comparison.json`)
1. **Writer A** updated Account 1 (seq 1), committed to PostgreSQL, but was deterministically paused before memory publication.
2. **Writer B** updated Account 2 (seq 2), committed to PostgreSQL, and completed in-memory publication.
3. **Telemetry During Delay:**
   - `Watermark`: **0**
   - `HighestPublishedSeq`: **2**
   - `InFlightCount`: **1 (seq 1)**
   - The contiguous tracker held the watermark at 0, preventing the global recovery checkpoint from jumping to 2.
4. **Crash Injection & Recovery:**
   - Writer A was terminated to simulate process failure prior to publication.
   - A recovery coordinator initialized and executed `ReconcilePendingMutationsAsync()` from watermark 0.
   - Recovery replayed **both seq 1 and seq 2** (2 events replayed).
   - In-memory state of Account 1 converged to `balance=50, active=false (Ineligible)`, exactly matching PostgreSQL authoritative state.
5. **Conclusion:** Delayed committed events are never skipped. The architecture reliably distinguishes between rollback gaps (which advance) and delayed committed events (which hold the watermark).

---

## 3. Metadata Pruning & Memory Layout Reconciliation

### 3.1 Units Correction Analysis
The previous audit reported that 50,000,000 entries in `ConcurrentDictionary<long, long>` at 60.89 bytes per entry consumed "2,903 MB decimal". This conflated SI decimal units with binary units:

$$\text{Total Bytes} = 50{,}000{,}000 \times 60.89 \text{ bytes} = 3{,}044{,}500{,}000 \text{ bytes}$$

- **Decimal Gigabytes (SI, $10^9$ bytes):**
  $$\frac{3{,}044{,}500{,}000}{1{,}000{,}000{,}000} \approx \mathbf{3.045 \text{ GB decimal}}$$
- **Binary Mebibytes (IEC, $2^{20} = 1{,}048{,}576$ bytes):**
  $$\frac{3{,}044{,}500{,}000}{1{,}048{,}576} \approx \mathbf{2{,}903.5 \text{ MiB binary}}$$
- **Binary Gibibytes (IEC, $2^{30} = 1{,}073{,}741{,}824$ bytes):**
  $$\frac{3{,}044{,}500{,}000}{1{,}073{,}741{,}824} \approx \mathbf{2.835 \text{ GiB binary}}$$

### 3.2 Category-Plane & Active-Plane Memory Layout Reconciliation
In `EapPackedCategoryPlane.cs`, storage is allocated across two unmanaged arrays:
1. `_categories`: `(maxId + 1) / 2` bytes (4 bits per account).
2. `_activeBits`: `(maxId + 63) / 64 * 8` bytes (1 bit per account).

For 50,000,000 accounts, the exact layout is:

| Plane Component | Bits / Acct | Total Bytes | Size Decimal (MB) | Size Binary (MiB) |
| :--- | :--- | :--- | :--- | :--- |
| **Category Buckets** (`_categories`) | 4 | 25,000,000 | 25.00 MB | 23.84 MiB |
| **Active Bitset** (`_activeBits`) | 1 | 6,250,000 | 6.25 MB | 5.96 MiB |
| **Combined Category Plane** | **5** | **31,250,000** | **31.25 MB** | **29.80 MiB** |
| **Separate Boolean Plane** (`_bitmap`) | **1** | **6,250,000** | **6.25 MB** | **5.96 MiB** |
| **Total Resident Unmanaged Planes** | **6** | **37,500,000** | **37.50 MB** | **35.76 MiB** |

> [!NOTE]
> The primary Boolean Bitmap operates at **strictly 6.25 MB decimal (5.96 MiB)** for 50M records. The packed Category Plane (including active bitset) operates at **31.25 MB decimal (29.80 MiB)**.

### 3.3 Safe Metadata Pruning Verification (`metadata_pruning_and_memory_reconciliation.json`)
- Loaded 1,000 mutations with simulated sequence gaps (incrementing by 3).
- Before pruning: 1,000 entries in `_accountSequences`. Watermark: 3,000.
- `PruneMetadataBelowWatermark()` executed:
  - 1,000 entries pruned.
  - Steady-state entries remaining: **0 entries (< 5 KB)**.
- Obsolete write rejection test:
  - New mutation published at seq 3005 for Account 50.
  - Obsolete write arriving with seq 3002 was **safely rejected**.
- **Conclusion:** Pruning below watermark reclaims 100% of inactive metadata without removing the guards necessary to prevent stale overwrites.

---

## 4. Shadow-Mode Contract Stress Verification

To ensure that the shadow pilot contract remains completely reliable beyond short runs, we executed a 1,000-query stress benchmark with injected outbox lag and recovery cycles (`shadow_mode_contract_verification.json`):

```mermaid
sequenceDiagram
    autonumber
    participant App as Application Client
    participant Pilot as EapShadowPilot
    participant PG as PostgreSQL (Authoritative)
    participant EAP as EAP Projection

    Note over App,EAP: Phase 1: 500 Normal Queries (Steady State)
    App->>Pilot: Evaluate(AccountId)
    Pilot->>PG: Authoritative Query
    Pilot->>EAP: Shadow Dark Query
    Pilot-->>App: Return PG Decision (100% Match)

    Note over App,EAP: Phase 2 & 3: Outbox Lag Injected (Lag = 50 > Max 20)
    Pilot->>Pilot: CheckOutboxLagAsync() -> Trip Circuit Breaker to OPEN
    App->>Pilot: Evaluate(AccountId)
    Pilot->>PG: Authoritative Query
    Note over Pilot: EAP Shadow Bypassed Completely
    Pilot-->>App: Return PG Decision (100% Availability)

    Note over App,EAP: Phase 4 & 5: Recovery & Outbox Catchup
    EAP->>PG: ReconcilePendingMutationsAsync (50 rows in 1.66 ms)
    Pilot->>Pilot: Reset Circuit Breaker to CLOSED
    App->>Pilot: Evaluate(AccountId)
    Pilot->>PG: Authoritative Query
    Pilot->>EAP: Shadow Dark Query
    Pilot-->>App: Return PG Decision (100% Match Resumed)
```

### 4.1 Measured Stress Telemetry

| Parameter | Telemetry Measurement | Note |
| :--- | :--- | :--- |
| **Total Authoritative Queries Processed** | **1,000** | 100% served by PostgreSQL |
| **Total Shadow Comparisons Evaluated** | **800** | (200 bypassed while circuit breaker was open) |
| **Shadow Decision Matches** | **799** | Steady state matching |
| **Shadow Decision Mismatches** | **1** | Transient during initial lag injection |
| **Match Fidelity** | **99.88%** | Authoritative answers never compromised |
| **Circuit Breaker Trip on Lag** | **Confirmed** | Tripped immediately when lag reached 50 |
| **Recovery Outbox Replay Duration** | **1.66 ms** | 50 events replayed |
| **Post-Recovery Lag** | **0 events** | Fully caught up |
| **PostgreSQL Latency (Mean)** | **555.93 µs** | Authoritative database call |
| **EAP Shadow Latency (Mean)** | **0.35 µs (352.7 ns)** | 1,576x faster than PostgreSQL |

---

## 5. Answers to Mandatory Audit Questions

### 1. Does the watermark advance after permanent rollback gaps?
**Yes.** PostgreSQL outbox sequences allocated by aborted transactions are discarded via `CancelInFlight` or never registered. When the next committed transaction publishes, `_inFlight.Count == 0`, and the watermark advances directly to the highest published sequence without stalling or waiting.

### 2. Can a delayed committed event ever be skipped?
**No.** Any mutation that commits to PostgreSQL registers into `_inFlight`. As long as its in-memory publication is delayed, `ContiguousWatermarkTracker` holds the watermark at $\min(\text{inFlight}) - 1$. If a crash occurs, recovery queries `WHERE seq_id > Watermark`, ensuring the delayed committed row is read from PostgreSQL outbox and incorporated.

### 3. Does recovery converge to the correct PostgreSQL state?
**Yes.** Repeatable Read snapshot hydration combined with contiguous watermark recovery and monotonic publication guards ensures 100% deterministic convergence to PostgreSQL authoritative state. Tested across restarts (2.68 ms hydration) and crash recovery.

### 4. Is metadata reclamation safe and bounded?
**Yes.** Reclaiming metadata entries where `seqId <= Watermark` leaves steady-state metadata at 0 to 50 entries (< 5 KB), preserving the **6.25 MB decimal** Boolean Bitmap footprint and **31.25 MB decimal** Category Plane footprint for 50,000,000 accounts.

### 5. Is the shadow pilot still safe to continue?
**Yes.** The shadow pilot maintains PostgreSQL authoritative routing at all times, gracefully bypasses shadow reads during lag or unhealthy states, and resumes transparently upon outbox catchup without leaking exceptions or modifying caller decisions.
