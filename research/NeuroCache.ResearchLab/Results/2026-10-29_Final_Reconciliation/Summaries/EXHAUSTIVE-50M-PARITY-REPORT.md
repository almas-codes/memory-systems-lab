# NeuroCache ResearchLab — Exhaustive 50M Parity & Oracle Verification Report

**Execution Date:** 2026-10-29 18:05:49 UTC  
**Target Dataset:** `eap_audit_20261028.accounts_50m` (50,000,000 records)  
**Candidate Tested:** `PROD-CANDIDATE-4BIT-EXCEPTION-SEQLOCK-001`  
**Oracle Baseline:** `PureCodeOracle` & `QueryFamily.EvaluatePredicate`  
**Scan Mode:** **100% EXHAUSTIVE (Zero Sampling)**  

## 1. Parity Audit Summary

| Metric | Value | Audit Requirement | Compliance Status |
|---|---|---|:---:|
| **Records Scanned from DB** | 50,000,000 | Exactly 50,000,000 | **MATCH** |
| **Records Encoded into Candidate** | 50,000,000 | 50,000,000 | **MATCH** |
| **Records Compared with Oracle** | 50,000,000 | 50,000,000 (Exhaustive) | **MATCH** |
| **Total Predicate Comparisons** | 400,000,000 | 400,000,000 (50M x 8) | **MATCH** |
| **Predicate Matches** | 400,000,000 | 400,000,000 (100.0%) | **100.0% MATCH** |
| **Predicate Mismatches** | 0 | 0 | **ZERO DEFECTS** |
| **Skipped Comparisons** | 0 | 0 | **NONE** |
| **Failed Reads / Fallbacks** | 0 | 0 | **NONE** |
| **Streaming SHA-256 Digest** | `265D6E5AEC71F23EA24C7D74B6D6F60C8897411A0838028833DB72331BC5E3CA` | Deterministic | **VERIFIED** |
| **Scan & Verification Duration** | 40.20 seconds | Under 120s | **1,243,877 rec/s** |

## 2. Exhaustive Answer-Pattern Distribution

Hashing Format: `LittleEndian_Int64_ID (8 bytes) + Byte_Pattern (1 byte) = 9 bytes per record`  

| Rank | Pattern (Hex) | Pattern (Binary) | Account Count | Percentage | Semantic Description |
|:---:|:---:|:---:|---:|---:|:---|
| 1 | `0x00` | `00000000` | 2,499,756 | 4.9995% | Inactive, Balance < 100 |
| 2 | `0x01` | `00000001` | 2,499,753 | 4.9995% | Inactive, 100 <= Balance < 500 |
| 3 | `0x03` | `00000011` | 3 | 0.0000% | Inactive, Balance >= 500 |
| 4 | `0x80` | `10000000` | 22,500,241 | 45.0005% | Active, Balance < 100 (Micro Tier) |
| 5 | `0xC1` | `11000001` | 22,500,137 | 45.0003% | Active, 100 <= Balance < 250 (Basic Tier) |
| 6 | `0xF3` | `11110011` | 5 | 0.0000% | Active, 500 <= Balance < 750 (Mid Tier) |
| 7 | `0xFB` | `11111011` | 100 | 0.0002% | Active, 750 <= Balance < 900 (Upper Tier) |
| 8 | `0xFF` | `11111111` | 5 | 0.0000% | Active, Balance >= 900 (High Tier) |

**Sum of Frequencies:** 50,000,000 / 50,000,000 (100.0%)  
**Distinct Classes Observed:** 8 out of 256 possible.  
