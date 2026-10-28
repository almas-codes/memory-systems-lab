# Benchmark Methodology and Integrity

## 1. Principles of Physical Honesty

1. **No Phantom Wins**: An NVMe SSD cannot beat already-resident, correctly implemented in-memory RAM on raw access latency. The advantage of a hybrid or storage-backed architecture must come from:
   - Avoiding unnecessary storage I/O through pure recomputation.
   - Reducing metadata overhead to fit 3-4x more records into the same RAM budget.
   - Eliminating intermediate object allocations and GC pauses.
   - Compressing the critical-path read depth from 2 dependent reads (index + extent) to 1 aligned read (capsule).
2. **Physical Separation of Setup and Measurement**: Dataset generation, index construction, warm-up runs, and integrity verification are strictly isolated from the measured request interval.
3. **Controlled Concurrency**: Concurrency is fixed across all competing candidates within each experiment run (single-threaded for microbenchmarks, 16 logical threads for multi-threaded full matrix runs).
4. **Independent Repetitions and Quantile Tracking**: Latency is tracked with high-precision timestamping (`Stopwatch.GetTimestamp()` with 10MHz frequency). Quantiles (p50, p95, p99, p99.9) are calculated from full sample histograms rather than relying on misleading averages.
5. **Real-World Strict Budgets**: Process working set and GC allocations are monitored. Strategies are required to enforce physical RAM budgets rather than allowing unbounded heap growth.
