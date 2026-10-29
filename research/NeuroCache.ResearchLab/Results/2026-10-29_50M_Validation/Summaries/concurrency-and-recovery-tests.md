# NeuroCache ResearchLab — Concurrency, Seqlock & Recovery Gate Results

**Date:** 2026-10-29 17:30:23 UTC  

| Concurrency Scenario | Status | Reads Evaluated | Writes Evaluated | Torn Reads | Lost Writes | Details |
|:---|:---:|---:|---:|---:|---:|:---|
| REC-03 Seqlock Monotonicity & Anti-Torn-Read | PASSED | 5,401,465 | 15,707,473 | 0 | 0 | Processed 5,401,465 reads and 15,707,473 contended writes; ZERO torn reads detected |
| Adjacent Packed Nibble CAS Isolation | PASSED | 15,162,050 | 27,345,388 | 0 | 0 | Simultaneous cross-thread CAS updates to even/odd accounts sharing byte K executed without corruption |
| ContiguousWatermarkTracker Rollback Gap & Monotonicity | PASSED | 0 | 6 | 0 | 0 | In-flight tracking verified: out-of-order publications held back correctly; rollback gap 5 bypassed safely without stalls |
| Crash Mid-Publication Outbox Replay | PASSED | 0 | 50 | 0 | 0 | Replayed 50 mutations from sequence 1..50; Restored watermark to 50 with zero oracle divergence |
