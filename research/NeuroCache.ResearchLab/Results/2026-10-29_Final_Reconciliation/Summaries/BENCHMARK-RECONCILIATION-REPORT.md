# NeuroCache ResearchLab — Benchmark Reconciliation & Integrated Tournament Report

**Date:** 2026-10-29 18:07:13 UTC  
**Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Host Platform:** AMD Ryzen 7 5825U (8 Physical Cores, 16 Logical Processors, 16 MB L3 Cache)  
**Stopwatch Resolution:** 10,000,000 Hz (100.0 ns tick quantum)  

## 1. Resolution of Throughput & Latency Inconsistencies

The previous contradiction between 37.09 Mops/s throughput (~27 ns/op) and reported 100 ns p50 latency is resolved:
- On Windows x64, `Stopwatch.Frequency` is 10 MHz, providing a minimum single-tick resolution of **100 nanoseconds**.
- Measuring individual sub-100ns operations with `Stopwatch.GetTimestamp()` experiences 100ns quantization error.
- By measuring batches of 100 operations, single-tick quantization is divided by 100, providing **1.0 ns precision**.
- Both throughput-derived mean latency and batch-sampled latency percentiles are now reported side-by-side.

## 2. Reconciled 50M Candidate Tournament Scorecard

| Candidate ID | ST Read (Mops/s) | ST Mean (ns) | ST p50 (ns) | ST p99 (ns) | MT 4T (Mops/s) | MT 8T (Mops/s) | MT 16T (Mops/s) | Zipf 8T (Mops/s) | Seq Scan (Mops/s) | ST Write (Mops/s) | MT8 Write (Mops/s) | Adj CAS Write (Mops/s) | Hot-ID Write (Mops/s) | Mixed 99/1 (Mops/s) | Mixed 95/5 (Mops/s) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `REP-CJB-001` | 33.64 | 29.7 | 22.0 | 89.0 | 45.90 | 90.00 | 115.94 | 114.13 | 138.21 | 48.80 | 59.59 | 171.50 | 46.04 | 91.61 | 96.83 |
| `SEM-BITMAPS-001` | 10.74 | 93.1 | 94.0 | 248.0 | 42.05 | 78.60 | 105.78 | 103.46 | 95.18 | 2.80 | 9.64 | 69.25 | 3.57 | 72.84 | 306.45 |
| `SEM-CLASS4-003` | 37.85 | 26.4 | 21.0 | 60.0 | 32.56 | 88.73 | 97.03 | 100.91 | 210.18 | 31.67 | 61.31 | 182.40 | 8.62 | 110.25 | 103.97 |
| `SEM-CLASS3-004` | 8.48 | 117.9 | 94.0 | 296.0 | 30.44 | 78.09 | 96.97 | 106.14 | 103.69 | 4.96 | 16.76 | 32.22 | 7.73 | 100.42 | 92.10 |
| `PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001` | 16.50 | 60.6 | 53.0 | 148.0 | 29.77 | 62.76 | 85.07 | 93.58 | 60.87 | 11.11 | 9.12 | 9.72 | 4.49 | 40.66 | 32.91 |
| `COMBO-SEM-SPARSE-002` | 26.88 | 37.2 | 40.0 | 55.0 | 31.98 | 67.04 | 81.18 | 98.75 | 82.00 | 14.71 | 7.16 | 9.84 | 5.85 | 45.39 | 36.07 |
| `SEM-BYTEVEC-002` | 38.10 | 26.2 | 24.0 | 43.0 | 158.43 | 218.95 | 71.91 | 117.45 | 295.77 | 51.98 | 95.24 | 299.45 | 51.91 | 406.53 | 371.95 |
