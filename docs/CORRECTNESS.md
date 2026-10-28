# Correctness, Validity Contracts, and Crash Recovery

## 1. Explicit Validity Contracts

In high-performance caching systems, returning stale or corrupted data renders performance gains completely invalid.

`NeuroCache.ResearchLab` enforces the following validity contracts:

1. **Exact Key Matching**: Hash collisions or index slot approximations are never treated as proof of identity. Every returned record verifies `header.Key == requestedKey`.
2. **Payload Checksum Integrity**: Every payload is sealed with a 64-bit `XxHash3` digest in the header. Upon retrieval, the payload checksum is verified before returning to the caller.
3. **State Witness Publication Boundary**:
   - Each record belongs to a deterministic Witness Domain (`DomainId = Key % 1024`).
   - Mutations increment the atomic State Witness generation:
     `Interlocked.Increment(ref _witnesses[domain].Generation);`
   - Any derived answer or cached claim observing generation $G < G_{current}$ is instantly invalidated in $O(1)$.
   - Readers never observe new authoritative records marked valid under obsolete witness generations.

---

## 2. Differential Verification

Every candidate strategy is continuously tested against the `DirectBaselineStrategy` (the authoritative ground truth).
For every operation ($Get$, $GetProjection$, $ComputeDerived$, $EvaluatePredicate$):
$$\text{Output}_{\text{candidate}}(Key) \equiv \text{Output}_{\text{authoritative}}(Key)$$
Any discrepancy immediately fails the automated correctness suite with the exact failing seed, key, and snapshot.

---

## 3. Crash and Recovery Protocol

Interrupted writes during extents append or metadata updates are handled by the `JournalRecoveryEngine`:
- Incomplete records at the tail of a segment file are detected via header magic (`0x4E435243`) and payload checksum validation.
- Trailing corrupted or partial bytes are automatically truncated to the last clean record boundary via `fs.SetLength(validLength)` and `fs.Flush(flushToDisk: true)`.
- Re-opened stores verify that all reachable index entries point to valid, checksum-verified records.
