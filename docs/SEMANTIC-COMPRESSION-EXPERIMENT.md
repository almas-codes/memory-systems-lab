# NeuroCache ResearchLab — Adaptive Semantic Compression: Experiment Specification

**Date:** October 29, 2026  
**Repository:** `almas-codes/memory-systems-lab`  
**Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Environment:** Windows 11, .NET 10.0, PostgreSQL 17.2, 16 GB RAM  
**Author:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## 1. Research Objective & Hypothesis

The foundation of the Early Access Path (EAP) demonstrated that a single 1-bit Boolean bitmap is an exceptionally fast and compact representation for a single precomputed query answer (requiring only 6.25 MB decimal / 5.96 MiB binary for 50M records). However, production applications rarely ask a single static question. They evaluate families of related business predicates: eligibility, balance tiers, activity status, risk thresholds, and compliance gates.

A naive multi-query cache allocates an independent 1-bit bitmap or key-value entry for each declared query. For 8 queries on 50 million records, 8 independent bitmaps consume 50.00 MB decimal (47.68 MiB binary), and answering all 8 queries for a given record requires touching 8 separate memory locations across disjoint cache lines.

### Core Hypothesis
> **NeuroCache can automatically discover the smallest practical exact representation for a declared family of queries, exploiting cross-query semantic correlation to represent all answers in materially less memory while preserving $O(1)$ random access and nanosecond lookup latency.**

---

## 2. Phase 0 — Security Boundary: Dedicated Least-Privilege Roles

To ensure application security and prevent accidental DDL or destructive mutations, superuser access (`postgres`) is strictly eliminated from all runtime cache and application paths. Dedicated non-superuser roles were implemented and enforced in PostgreSQL:

1. **`neurocache_app` (Read-Only Application Role):**
   - Attributes: `NOSUPERUSER NOCREATEDB NOCREATEROLE LOGIN`
   - Permissions: `GRANT CONNECT ON DATABASE neurocache_eap_lab`
   - Schema: `GRANT USAGE ON SCHEMA eap_audit_20261028, eap_lab`
   - Tables: `GRANT SELECT ON accounts_50m, mutation_log`
   - Restrictions: Explicitly denied `INSERT, UPDATE, DELETE, TRUNCATE, DROP, ALTER` (raises PostgreSQL `42501: permission_denied`).
2. **`neurocache_writer` (Coordinator Mutation Role):**
   - Attributes: `NOSUPERUSER NOCREATEDB NOCREATEROLE LOGIN`
   - Permissions: `GRANT CONNECT ON DATABASE neurocache_eap_lab`
   - Schema: `GRANT USAGE ON SCHEMA eap_audit_20261028`
   - Tables: `GRANT SELECT, UPDATE ON accounts_50m; GRANT SELECT, INSERT ON mutation_log; GRANT USAGE, SELECT ON SEQUENCE mutation_log_seq_id_seq`
   - Restrictions: Denied `DELETE, TRUNCATE, DROP, ALTER` or schema DDL.
3. **Automated Provisioning & Connection Sanitization:**
   - Managed idempotently via `PostgreSqlPreflight.EnsureLeastPrivilegeRolesAsync()`.
   - Accessors: `PostgreSqlPreflight.GetAppConnectionString()` and `GetWriterConnectionString()`.
   - Sanitizer: `PostgreSqlPreflight.SanitizeConnectionString()` masks passwords (`Password=********`) to eliminate credential leakage in logs and telemetry.

---

## 3. Phase 1 — Exact Query Family Definition

The experiment defines an exact family of 8 deterministic Boolean queries evaluated against `eap_audit_20261028.accounts_50m` (fields: `id BIGINT`, `balance INT`, `active BOOLEAN`, `version BIGINT`):

| Query ID | Identifier | Predicate Logic | SQL Clause | Bit Position |
| :--- | :--- | :--- | :--- | :---: |
| $Q_1$ | `Q1_Active` | Account is active | `active` | Bit 7 (0x80) |
| $Q_2$ | `Q2_ActiveAndBalGte100` | Active and balance $\ge 100$ | `(balance >= 100 AND active)` | Bit 6 (0x40) |
| $Q_3$ | `Q3_ActiveAndBalGte250` | Active and balance $\ge 250$ | `(balance >= 250 AND active)` | Bit 5 (0x20) |
| $Q_4$ | `Q4_ActiveAndBalGte500` | Active and balance $\ge 500$ | `(balance >= 500 AND active)` | Bit 4 (0x10) |
| $Q_5$ | `Q5_ActiveAndBalGte750` | Active and balance $\ge 750$ | `(balance >= 750 AND active)` | Bit 3 (0x08) |
| $Q_6$ | `Q6_ActiveAndBalGte900` | Active and balance $\ge 900$ | `(balance >= 900 AND active)` | Bit 2 (0x04) |
| $Q_7$ | `Q7_BalGte500` | Balance $\ge 500$ regardless of active status | `(balance >= 500)` | Bit 1 (0x02) |
| $Q_8$ | `Q8_BalGte100` | Balance $\ge 100$ regardless of active status | `(balance >= 100)` | Bit 0 (0x01) |

### Mathematical Ceiling on Distinct Classes
While an unconstrained 8-query vector could produce $2^8 = 256$ distinct combinations, the predicates in real domains are semantically coupled and monotonic:
- $Q_6 \implies Q_5 \implies Q_4 \implies Q_3 \implies Q_2 \implies Q_1$
- $Q_4 \implies Q_7 \implies Q_8$
- If `active = false`, then $Q_1..Q_6$ are guaranteed to be `false`, leaving only the 3 balance tiers of $Q_7$ and $Q_8$.
- If `active = true`, $Q_1$ is `true`, leaving the 6 progressive balance bands.

Therefore, the maximum theoretical distinct answer classes $C_{max} \le 9$.  
Because $9 \le 16 = 2^4$, the full 8-query vector can be packed into a single **4-bit nibble** (2 records per physical byte), cutting payload memory by exactly 50%.

---

## 4. Phase 2 — Architecture Candidates Under Test

### Candidate A: 8 Independent Boolean Bitmaps
- **Structure:** 8 separate `EapBooleanBitmap` instances, each storing $(N + 63) / 64$ `ulong` words.
- **Single Query:** Reads 1 bit from the designated bitmap array.
- **All 8 Queries:** Performs 8 disjoint memory reads across 8 distinct memory allocations and composes an 8-bit byte.
- **Memory Footprint:** $8 \times 6.25\text{ MB} = 50.00\text{ MB}$ decimal (47.68 MiB binary).

### Candidate B: Packed 8-Bit Byte Answer Pattern
- **Structure:** Contiguous `byte[] _patterns = new byte[maxId + 1]`.
- **Single Query:** Reads 1 byte at index `id`, masks bit `(pattern >> (7 - queryIndex)) & 1`.
- **All 8 Queries:** Reads 1 byte directly at index `id` (single memory read).
- **Memory Footprint:** $50,000,001\text{ bytes} = 50.00\text{ MB}$ decimal (47.68 MiB binary).

### Candidate C: Adaptive Semantic Answer-Class Plane
- **Structure:** Compact 4-bit nibble array `byte[] _data = new byte[(maxId + 2) / 2]` paired with an in-memory Semantic Class Dictionary.
- **Dictionary:** Fast dual lookup arrays:
  - `_classToPattern[16]`: Maps `ClassId (0..15)` $\to$ `AnswerPattern (8-bit byte)`.
  - `_patternToClass[256]`: Maps `AnswerPattern (0..255)` $\to$ `ClassId (0..15)`.
- **Single Query:** Computes byte offset `id >> 1`, extracts 4-bit nibble, looks up class dictionary in L1 cache, extracts bit.
- **All 8 Queries:** Computes byte offset `id >> 1`, extracts 4-bit nibble, looks up `_classToPattern[classId]`, returns pattern byte directly.
- **Concurrency & CAS:** Atomic compare-and-swap via `Interlocked.CompareExchange` on the shared byte ensures lock-free, race-free updates between adjacent even and odd records.
- **Memory Footprint:** $25,000,001\text{ bytes}$ (Payload) $+ 272\text{ bytes}$ (Dictionary) $= 25.000272\text{ MB}$ decimal (23.84 MiB binary). **Exact 50.0% reduction.**

### Candidate D: Reference Single Boolean Bitmap
- **Structure:** Single `EapBooleanBitmap` instance representing $Q_1$.
- **Purpose:** Serves as the lower baseline bound for 1-query storage overhead (6.25 MB decimal / 5.96 MiB binary).

### Candidate E: Direct PostgreSQL Baseline
- **Structure:** Authoritative relational table `eap_audit_20261028.accounts_50m` queried via Npgsql using covering B-tree indexes.
- **Purpose:** Ground truth anchor measuring disk-backed relational query latency and throughput.

---

## 5. Experimental Protocol & Execution Strategy

1. **Preflight & Least-Privilege Verification:** Verify roles `neurocache_app` and `neurocache_writer`.
2. **Dataset Distribution Profiling:** Full scan of `accounts_50m` in PostgreSQL to enumerate all actual answer patterns.
3. **Memory Footprint Audit:** Exact byte accounting separating payload, dictionary, and runtime structures.
4. **Synchronized Hydration:** Sequential streaming of 50M rows into Candidates A, B, C, D under consistent snapshot isolation.
5. **Exact Parity Gate:** 100,000 random account evaluations across all 8 queries, confirming 100.0% parity (0 mismatches).
6. **Benchmark Matrix:**
   - Threads: 1, 4, 8, 16 logical workers.
   - Access Patterns: Sequential vs Uniform 64-bit Random across 50,000,000 IDs.
   - Query Types: Single-Query vs All-8-Queries.
   - Operations: 15,000,000 single-thread ops; 30,000,000 multi-thread ops.
7. **Incremental Updates & Concurrency:**
   - Single-account mutation throughput (2,000,000 updates per candidate).
   - Dynamic registration of novel patterns.
   - High-contention CAS race between adjacent even/odd IDs sharing a physical byte.
