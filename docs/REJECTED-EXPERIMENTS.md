# NeuroCache ResearchLab — Rejected Experiments & Dead Ends

**Repository:** `almas-codes/memory-systems-lab`  
**Date:** October 29, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## 1. Executive Summary

Negative results are as critical to engineering science as positive results. Out of the 68 candidate experiments investigated in the NeuroCache ResearchLab, several architectures that appeared theoretically attractive on paper proved unviable, inefficient, or counterproductive when tested under real concurrent workloads on 10M and 50M records.

This document details the rejected experiments, explaining the exact empirical failure modes, profiling data, and architectural reasons for their elimination.

---

## 2. Catalog of Rejected Architectures

### 2.1. Candidate: `COMBO-05` / `EXEC-02` (Hot Materialization with Cold Fallback)
- **The Hypothesis:** Only 10% of accounts receive 90% of queries in real workloads. By materializing answers only for hot accounts and computing cold answers on the fly from backing records, memory should drop by 90%.
- **The Empirical Reality:**
  - Memory on 10M accounts: **320.00 MB** (305.18 MiB) — **32x larger** than flat semantic classes (10 MB)!
  - Single-Thread Read: **7.13 Mops/s** (vs 27.81 Mops/s for 4-bit classes).
  - Multi-Thread Read: **18.84 Mops/s** (vs 84.04 Mops/s for 4-bit classes).
- **Why It Failed:**
  Tracking access frequency and maintaining sparse hash maps introduces catastrophic pointer and node overhead. A `ConcurrentDictionary<long, byte>` requires 32 to 48 bytes of metadata per entry. When access patterns drift, tracking counters allocate millions of integer nodes. The CPU spent more time resolving hash collisions and cache misses than simply reading a flat 0.50-byte array.
- **Verdict:** **REJECTED** for dense universes. Flat sub-byte arrays beat sparse caching.

---

### 2.2. Candidate: `COMBO-03` / `REC-01` (Copy-On-Write Page Generations in High-Contention Writes)
- **The Hypothesis:** Copy-On-Write (CoW) page directories provide lock-free snapshot reads without seqlocks. Mutations simply allocate a new copy of a 4KB page and swap the root pointer.
- **The Empirical Reality:**
  - Memory under burst writes: **291.00 MB** (277.52 MiB).
  - Multi-Thread Write Throughput: **0.40 Mops/s** in pure CoW; **8.44 Mops/s** in bounded overlay.
- **Why It Failed:**
  Every single write to an account in a 4096-account page forces a 4,096-byte array allocation and memory copy. Mutating 1,000,000 accounts creates 4 Gigabytes of transient heap allocations, triggering aggressive .NET Gen 0 / Gen 1 garbage collection pauses. When multiple worker threads mutate accounts on the same page, CAS spin loops fail repeatedly.
- **Verdict:** **REJECTED** as primary mutation engine. Replaced by `REC-03` (Seqlock in-place mutation).

---

### 2.3. Candidate: `EXEC-01` (Zero Storage Predicate JIT Compiler)
- **The Hypothesis:** Eliminate RAM entirely (0 bits/account) by compiling SQL predicates into JIT IL closures that evaluate on raw database records.
- **The Empirical Reality:**
  - Memory: **0 bytes**.
  - Read Throughput: **5.2 Mops/s** (a **16x drop** compared to 84 Mops/s).
  - Latency: **192 nanoseconds** per query (vs 11.9 ns for cached classes).
- **Why It Failed:**
  Evaluating boolean logic over raw record fields requires loading multiple fields (`Balance`, `Active`, `Tier`, `RiskScore`) from memory, executing branch instructions, and evaluating comparison operators on every single read. For read-intensive workloads (>100k queries/sec), CPU saturation occurs rapidly.
- **Verdict:** **REJECTED** as primary cache. Retained only as cold-miss fallback for rare ad-hoc queries.

---

### 2.4. Candidate: `MEM-01` (Roaring Sparse/Dense Chunked Bitmaps)
- **The Hypothesis:** Roaring bitmaps compress sparse sets into sorted 16-bit integers and dense sets into 64-bit words, dynamically optimizing memory per 4096-bit chunk.
- **The Empirical Reality:**
  - Memory on 50M dense accounts: **50.2 MB** (slightly larger than flat 50.0 MB array).
  - Read Throughput: **38.4 Mops/s** (vs 62.8 Mops/s for flat bitmaps).
- **Why It Failed:**
  In `accounts_50m`, account IDs are dense and contiguous (1..50,000,000). Every 4096-bit chunk had cardinality > 4096 / 16, causing Roaring bitmaps to convert every chunk into a dense bitset. The chunk directory and container object references added 0.2 MB of pointer overhead while introducing an extra pointer indirection on every query.

---

### 2.5. Candidate: `SEM-CLASS3-004` (3-Bit Semantic Classes as Primary Production Engine)
- **The Hypothesis:** Achieve maximum theoretical compression (3.00 bits/account, 18.75 MB for 50M records, -62.5% reduction) by packing 3-bit semantic classes across byte boundaries.
- **The Empirical Reality:**
  - Memory for 50M accounts: **18.75 MB** (17.88 MiB).
  - Single-Thread Read: **7.39 Mops/s** (vs **37.09 Mops/s** for 4-bit, a **5x slowdown**).
  - Single-Thread Write: **3.51 Mops/s** (vs **45.77 Mops/s** for 4-bit, a **13x slowdown**).
  - Adjacent-ID Contention Write: **31.32 Mops/s** (vs **169.20 Mops/s** for 4-bit).
- **Why It Was Rejected for Primary Cache:**
  Packing 3 bits into a continuous bitstream means that 3 out of every 8 accounts cross a physical byte boundary. Reading a boundary account requires two memory accesses, shifting, and a branch (`bitOffset <= 5`). Worse, updating an account crossing a byte boundary cannot be performed using a single hardware atomic CAS; it requires taking coarse stripe locks over shared boundary bytes, inducing thread serialization under concurrent writers.
- **Verdict:** **REJECTED** as primary operational cache engine. Retained as an archive or secondary read-only tier where memory is the sole constraint.

- **Verdict:** **REJECTED** for dense sequential database primary keys. Roaring bitmaps are advantageous only when primary keys are highly sparse or fragmented.

---

### 2.5. Candidate: `ALT-02` (Bloom Filter Negative Gate)
- **The Hypothesis:** A compact Bloom filter can fast-reject negative answers in 1-2 bits/account, falling back to database evaluation only for positives.
- **The Empirical Reality:**
  - False positive rate (at 2 bits/acc): ~39%.
  - Read Throughput: Unstable, with extreme tail latency spikes whenever false positives triggered database fallbacks.
- **Why It Failed:**
  A cache that serves false positives cannot be authoritative. To preserve exactness, every positive Bloom hit had to execute a full record verification. With a 39% false positive rate on queries where 50% of the database was negative, over 19% of all queries suffered massive latency penalties.
- **Verdict:** **REJECTED**. Exact semantic factorization (`SEM-03` / `COMBO-02`) provides 100% exactness in 4 bits without any false positives.
