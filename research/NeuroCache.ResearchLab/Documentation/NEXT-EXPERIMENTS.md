# NeuroCache ResearchLab — Next Research Experiments & Scale Horizon

**Repository:** `almas-codes/memory-systems-lab`  
**Date:** October 29, 2026  
**Research Lead:** Almas Khan (`almaskhanwazir@gmail.com`)

---

## 1. Executive Summary

Having established that **`COMBO-02` (4-Bit Semantic Classes with Sparse Exceptions)** and **`REC-03` (Seqlock Page Publication)** form the optimal architectural foundation for 50,000,000 records, this document outlines the next experimental roadmap to scale the technology toward 500M records and distributed multi-node production deployment.

---

## 2. Planned Research Tracks

### Track 1: Online Equivalence Class Re-Partitioning (Dynamic Query Addition)
- **Objective:** Enable registering a 9th or 10th query predicate at runtime without restarting the cache or taking global locks.
- **Experimental Design:**
  - When a new predicate is declared, analyze its correlation with existing equivalence classes.
  - If the new predicate splits existing classes such that total classes exceed 16, execute a background zero-copy promotion from 4-bit nibbles to 8-bit bytes (`SEM-08`).
  - Read queries continue serving from the old 4-bit plane until the atomic pointer swap to the 8-bit plane completes.

### Track 2: Hardware AVX-512 SIMD Acceleration for Bit-Sliced Arithmetic (`THR-02`)
- **Objective:** Accelerate bulk range scan queries (e.g. `COUNT(*) WHERE balance >= 500 AND active = true`).
- **Experimental Design:**
  - Utilize .NET 10 hardware intrinsics (`System.Runtime.Intrinsics.X86.Avx512F`).
  - Evaluate bit-sliced columnar integer planes 512 accounts at a time per AVX register.
  - Benchmark against native PostgreSQL sequential scans with parallel workers.

### Track 3: The 500-Million-Record Scaling Envelope
- **Objective:** Validate memory and latency scaling when accounts increase from 50M to 500M.
- **Target Envelope:**
  - At 4 bits per record, 500,000,000 records consume:
    $$\frac{500,000,000 \times 4 \text{ bits}}{8 \times 1,000,000} = 250.00 \text{ MB decimal (238.4 MiB binary)}.$$
  - Even at 500 million records, the entire database query cache consumes **less than 250 MB of RAM**!
  - Test OS page locking (`VirtualLock` on Windows / `mlock` on Linux) to prevent OS paging of cold memory pages under memory pressure.

### Track 4: Multi-Node Distributed Replication with Local Seqlocks
- **Objective:** Replicate the 25 MB state across multiple read replicas.
- **Experimental Design:**
  - PostgreSQL writes outbox events via logical replication or CDC (Debezium).
  - Each replica consumes transaction events, advances its local `ContiguousWatermarkTracker`, and publishes to its local Seqlock-partitioned memory array.
  - Verifies cross-node consistency and failover liveness.
