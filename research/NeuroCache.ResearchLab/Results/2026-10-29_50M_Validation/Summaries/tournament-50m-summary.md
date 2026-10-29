# NeuroCache ResearchLab: 50,000,000 Record Tournament Results

**Date:** 2026-10-29 17:32:41 UTC  
**Target Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Host Platform:** AMD Ryzen 7 5825U (8 Physical Cores, 16 Logical Processors, 16 MB L3 Cache)  
**Runtime:** .NET 10.0 x64, PostgreSQL 17.2  

## 1. Candidate Footprint & Construction Metrics

| Candidate ID | Candidate Name | Family | Payload (MB) | Metadata (KB) | Total (MB) | Total (MiB) | Bits/Acc | Init Time (s) |
|---|---|---|---|---|---|---|---|---|
| `SEM-CLASS3-004` | Three-Bit Semantic Classes (SEM-04) | SemanticEncoding | 18.75 | 1.8 | 18.75 | 17.88 | 3.00 | 4.32s |
| `COMBO-SEM-SPARSE-002` | Semantic Classes with Rare Exceptions (COMBO-02) | HybridCompositions | 25.00 | 0.1 | 25.00 | 23.84 | 4.00 | 3.07s |
| `SEM-CLASS4-003` | Four-Bit Semantic Classes (SEM-03) | SemanticEncoding | 25.00 | 0.3 | 25.00 | 23.84 | 4.00 | 1.16s |
| `SEM-BITMAPS-001` | Independent Bitmaps per Predicate (SEM-01) | SemanticEncoding | 50.00 | 0.1 | 50.00 | 47.68 | 8.00 | 3.05s |
| `SEM-BYTEVEC-002` | Byte-per-Record Answer Vector (SEM-02) | SemanticEncoding | 50.00 | 0.1 | 50.00 | 47.68 | 8.00 | 0.79s |
| `REP-CJB-001` | Correlated-Outcome Joint Bitmap (CJB) | Representations | 50.00 | 0.1 | 50.00 | 47.68 | 8.00 | 0.40s |
| `REC-SEQLOCK-003` | Seqlock Page Publication (REC-03) | Recovery | 50.00 | 763.0 | 50.78 | 48.43 | 8.13 | 1.99s |

## 2. Read Throughput & Latency Tournament

| Candidate ID | ST Read (Mops/s) | MT 8T (Mops/s) | MT 16T (Mops/s) | ST p50 (ns) | ST p99 (ns) | MT8 p50 (ns) | MT8 p99 (ns) | Zipf 8T (Mops/s) | Seq Scan (Mops/s) |
|---|---|---|---|---|---|---|---|---|---|
| `SEM-BYTEVEC-002` | 29.06 | 59.28 | 158.63 | 100.0 | 200.0 | 200.0 | 600.0 | 111.05 | 94.61 |
| `COMBO-SEM-SPARSE-002` | 29.52 | 42.80 | 100.60 | 100.0 | 200.0 | 200.0 | 600.0 | 111.19 | 88.19 |
| `REP-CJB-001` | 33.29 | 57.39 | 95.64 | 100.0 | 200.0 | 200.0 | 600.0 | 108.96 | 244.91 |
| `SEM-CLASS4-003` | 37.09 | 43.62 | 92.55 | 100.0 | 200.0 | 200.0 | 600.0 | 99.84 | 116.11 |
| `SEM-BITMAPS-001` | 9.43 | 44.16 | 89.21 | 200.0 | 700.0 | 200.0 | 700.0 | 101.51 | 103.84 |
| `SEM-CLASS3-004` | 7.39 | 54.23 | 78.45 | 200.0 | 700.0 | 200.0 | 600.0 | 51.03 | 134.06 |
| `REC-SEQLOCK-003` | 20.74 | 44.36 | 68.01 | 100.0 | 300.0 | 200.0 | 700.0 | 90.48 | 78.78 |

## 3. Write & Concurrency Tournament

| Candidate ID | ST Write (Mops/s) | MT8 Write (Mops/s) | Adj-Contention Write (Mops/s) | Mixed 99/1 (Mops/s) | Mixed 95/5 (Mops/s) | Working Set (MB) |
|---|---|---|---|---|---|---|
| `SEM-BYTEVEC-002` | 47.27 | 84.58 | 291.65 | 380.89 | 349.94 | 112 |
| `SEM-CLASS3-004` | 3.51 | 17.63 | 31.32 | 94.16 | 81.25 | 60 |
| `SEM-CLASS4-003` | 45.77 | 31.06 | 169.20 | 88.15 | 81.37 | 79 |
| `REP-CJB-001` | 49.48 | 59.83 | 303.26 | 83.93 | 89.71 | 143 |
| `SEM-BITMAPS-001` | 2.54 | 10.80 | 80.50 | 60.19 | 289.00 | 136 |
| `REC-SEQLOCK-003` | 6.71 | 13.13 | 14.35 | 50.27 | 41.79 | 185 |
| `COMBO-SEM-SPARSE-002` | 14.83 | 8.73 | 9.52 | 49.23 | 41.62 | 86 |
