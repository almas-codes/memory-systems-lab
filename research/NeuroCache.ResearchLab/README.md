# NeuroCache ResearchLab

A high-performance experimental research laboratory investigating semantic query cache compression, sub-byte representation densities, lock-free seqlock publication, and transaction-consistent recovery over massive relational datasets.

---

## 1. Project Overview

NeuroCache ResearchLab explores whether multiple exact query answers across massive datasets (50,000,000+ records) can be stored in radically less memory than independent boolean bitmaps while sustaining >75 million operations/second with strict serializability.

### Key Capabilities
- **68 distinct candidate architectures** across 11 families (Representations, Semantic Encoding, Thresholds, Mutations, Freshness, Execution, Memory, Adaptive Selection, Alternative Encodings, Recovery, and Hybrid Compositions).
- **Sub-Byte Compression:** Compresses 8 orthogonal business queries to **3 bits** (18.75 MB / 50M) and **4 bits** (25.00 MB / 50M) with zero false positives.
- **Tear-Free Publication:** Seqlock-partitioned 64-bit sequence counters (`REC-03`) eliminating torn reads during concurrent writes.
- **Transaction-Safe Recovery:** `ContiguousWatermarkTracker` eliminating rollback gap anomalies across asynchronous PostgreSQL commits.
- **Authoritative Parity:** 100% bit-for-bit parity verified against PostgreSQL 17.2 and code oracles across all 8 predicates.

---

## 2. Directory Structure

```
research/NeuroCache.ResearchLab/
├── AdaptiveSelection/         # ADAPT-01 to ADAPT-05
├── AlternativeEncodings/      # ALT-01 to ALT-05
├── Benchmarks/                # TournamentBenchmarkRunner.cs
├── ConcurrencyTests/          # Concurrency stress test suites
├── Contracts/                 # Core research interfaces (IPredicateEvaluator, etc.)
├── CorrectnessTests/          # Correctness suites comparing against oracles
├── Data/                      # AccountModel.cs & SampleDataGenerator.cs
├── Documentation/             # Mirrored research reports & analysis
├── FreshnessAndPublication/   # MUT-03 (Epoch Clock), MUT-07 (Vector Clock)
├── HybridCompositions/        # COMBO-01 to COMBO-06
├── MemoryAndLocality/         # MEM-01 to MEM-10
├── MutationMaintenance/       # MUT-01, MUT-02, MUT-04..MUT-10
├── Oracle/                    # PureCodeOracle.cs & PostgreSqlOracle.cs
├── QueryExecution/            # EXEC-01 to EXEC-10
├── Recovery/                  # REC-01 to REC-04
├── RecoveryTests/             # Crash injection and recovery tests
├── Representations/           # REP-01 to REP-07
├── Results/                   # Raw JSON logs & summary markdown tables
├── SemanticEncoding/          # SEM-01 to SEM-08
├── ThresholdEncoding/         # THR-01 to THR-03
├── NeuroCache.ResearchLab.csproj
└── Program.cs                 # Tournament CLI entry point
```

---

## 3. Build & Test Instructions

### Building the Project
```bash
dotnet build research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj
```

### Running All Automated Tests (94 Tests)
```bash
dotnet test NeuroCache.ResearchLab.slnx
```

### Running the Candidate Tournament Benchmark
```bash
# Run 1,000,000 accounts
dotnet run --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj -- --accounts 1000000 --threads 8 --iterations 1000000

# Run 10,000,000 accounts
dotnet run --project research/NeuroCache.ResearchLab/NeuroCache.ResearchLab.csproj -- --accounts 10000000 --threads 8 --iterations 500000
```

Raw JSON benchmarks are output to `Results/2026-10-29/Raw/` and formatted summaries are generated in `Results/2026-10-29/Summaries/`.

---

## 4. Documentation Index

Detailed research findings and technical reports are available in the `docs/` folder:
- [EXPERIMENT-REGISTER.md](../../docs/EXPERIMENT-REGISTER.md): Catalog of all 68 candidate experiments.
- [PRIOR-ART-AND-NOVELTY.md](../../docs/PRIOR-ART-AND-NOVELTY.md): Comparison against Redis, Postgres materialized views, and Arrow.
- [EXPERIMENT-MATRIX.md](../../docs/EXPERIMENT-MATRIX.md): Full matrix of memory, latency, and throughput trade-offs.
- [BUILD-AND-CORRECTNESS-REPORT.md](../../docs/BUILD-AND-CORRECTNESS-REPORT.md): 94-test verification report.
- [TOURNAMENT-RESULTS.md](../../docs/TOURNAMENT-RESULTS.md): Detailed 1M and 10M benchmark numbers.
- [WINNER-ANALYSIS.md](../../docs/WINNER-ANALYSIS.md): Comprehensive analysis of `COMBO-02` / `SEM-03` victory.
- [REJECTED-EXPERIMENTS.md](../../docs/REJECTED-EXPERIMENTS.md): Empirical dissection of discarded approaches.
- [NEXT-EXPERIMENTS.md](../../docs/NEXT-EXPERIMENTS.md): Scaling roadmap to 500M records.
