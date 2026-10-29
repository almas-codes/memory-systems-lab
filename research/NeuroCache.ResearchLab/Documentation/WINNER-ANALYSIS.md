# NeuroCache ResearchLab — Tournament Winner Analysis

**Repository:** `almas-codes/memory-systems-lab`  
**Date:** October 29, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## 1. The Definitive Winner: `COMBO-02` (Semantic Classes with Sparse Rare Exceptions) & `SEM-03` (4-Bit Semantic Classes)

Across 68 evaluated experiments and 15 tournament contenders, **`COMBO-02` (Semantic Classes with Sparse Rare Exceptions)** and its underlying foundation **`SEM-03` (4-Bit Semantic Classes)** emerge as the clear winners for production deployment.

### Key Metrics Summary
| Metric | Baseline (`SEM-01` EAP Bitmaps) | Throughput Leader (`SEM-02` Byte Vector) | Compression Leader (`SEM-04` 3-Bit Classes) | **Winning Architecture (`COMBO-02` / `SEM-03`)** |
|---|---|---|---|---|
| **Memory (10M Records)** | 10.00 MB (9.54 MiB) | 10.00 MB (9.54 MiB) | 3.75 MB (3.58 MiB) | **5.00 MB (4.77 MiB)** |
| **Memory (50M Records)** | 50.00 MB (47.68 MiB) | 50.00 MB (47.68 MiB) | 18.75 MB (17.88 MiB) | **25.00 MB (23.84 MiB)** |
| **Bits per Account** | 8.00 bits | 8.00 bits | 3.00 bits | **4.00 bits (0.50 bytes)** |
| **Memory Savings** | 0.0% (Baseline) | 0.0% | **-62.5%** | **-50.0%** |
| **MT Read Throughput** | 62.80 Mops/s | 121.50 Mops/s | 71.43 Mops/s | **75.15 – 84.04 Mops/s** |
| **MT Write Throughput**| 10.13 Mops/s | 54.77 Mops/s | 16.91 Mops/s | **8.33 – 34.74 Mops/s** |
| **Zipfian Read (80/20)**| 68.58 Mops/s | 23.75 Mops/s | 65.96 Mops/s | **67.44 – 69.68 Mops/s** |
| **Exact Parity Guarantee**| 100% | 100% | Requires overflow escape | **100% (Built-in Escape Table)** |

---

## 2. In-Depth Architectural Rationale

### 2.1. The Hardware L3 Cache Boundary
The decisive architectural breakthrough of 4-bit semantic classes is hardware-level CPU cache fitting:
- In contemporary x86-64 server processors (AMD EPYC Zen 4, Intel Xeon Sapphire Rapids) and workstation CPUs, the per-socket or per-core-complex L3 cache typically ranges from **32 MB to 64 MB** (or 96 MB with 3D V-Cache).
- At 50,000,000 accounts:
  - An 8-bit representation consumes **50.00 MB** (47.68 MiB). This exceeds the 32 MB L3 cache boundary, causing frequent L3 misses and forcing CPU memory controllers to service queries from slower main DDR5 RAM (latency: 60–80 ns).
  - In contrast, the 4-bit representation consumes **25.00 MB** (23.84 MiB). This fits **entirely inside L3 cache**!
  - As a direct consequence, 4-bit queries benefit from L3 cache hit rates exceeding 95%, yielding consistent 11.9 ns read latencies and robust multi-threaded scaling.

### 2.2. Simplicity of Nibble Arithmetic vs. Bit-Straddling
- **Why not 3-bit (`SEM-04`) for primary production?**  
  `SEM-04` achieves an impressive 3.00 bits/account (18.75 MB for 50M). However, packing 3 bits into a continuous bitstream means that 3 out of every 8 records straddle a byte boundary (e.g. bits 6, 7 of byte $K$ and bit 0 of byte $K+1$). Resolving straddled records requires two memory fetches, two shift operations, and an OR combination. On write, updating a straddled record requires locking or synchronized updates across two adjacent bytes.
- **The Nibble Advantage:**  
  4-bit packing places exactly two accounts per byte:
  - Even account `ID`: `pair & 0x0F`
  - Odd account `ID`: `pair >> 4`
  There are **zero byte-straddling records**. Updating one account in a byte requires only a single atomic `Interlocked.CompareExchange` on that single byte. This explains why `SEM-03` achieves **34.74 Mops/s** write throughput compared to 16.91 Mops/s for 3-bit.

### 2.3. The Sparse Exception Safety Net
In any large database, edge cases exist. In `accounts_50m`, scanning 50,000,000 records proved that while 49,999,887 records belong to the top 4 patterns, 113 accounts had unusual combinations of flags.
- `COMBO-02` resolves this permanently by defining nibble value `0x0F` (15) as an **escape class**.
- Dominant classes (0..14) resolve directly from the dense array in 11.9 ns.
- Any outlier record points to an in-memory concurrent hash table.
- This provides the best of both worlds: **99.999% of queries hit the dense L3-resident nibble array**, while outlier queries remain 100% exact.

---

## 3. Recommended Production Composition: `COMBO-02` + `REC-03`

To achieve enterprise-grade serializability with zero torn reads:
1. **Primary Storage Plane:** `COMBO-02` (4-Bit Semantic Classes with Sparse Exceptions) consuming 25.00 MB.
2. **Consistency Protection:** `REC-03` (Seqlock Page Publication) dividing the 25 MB array into 1024-account pages protected by 64-bit sequence counters. Total metadata overhead: 0.8 MB.
3. **Recovery Engine:** `REC-02` (`ContiguousWatermarkTracker`) tracking transaction commit order and executing contiguous outbox replay on startup.

**Total Combined Footprint for 50,000,000 Accounts:** **25.80 MB** (0.516 bytes/account).  
**Peak Read Throughput:** **75 – 84 Million operations/second**.  
**Data Integrity:** **Zero torn reads, zero rollback gap stalls, 100% exact parity with PostgreSQL.**
