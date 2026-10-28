using System;
using System.Collections.Generic;
using NeuroCache.Core;

namespace NeuroCache.Benchmarks;

public enum WorkloadDistribution
{
    Uniform,
    ZipfSkewed,
    Sequential,
    WorkingSetFit,
    WorkingSet2xRAM,
    WorkingSet10xRAM,
    Bursty,
    Adversarial
}

public sealed class WorkloadRequest
{
    public OperationType OpType;
    public ulong Key;
    public ulong Param;
    public DataRecord? MutationRecord;
}

public sealed class WorkloadGenerator
{
    public static List<WorkloadRequest> GenerateWorkload(
        int totalRequests,
        long datasetRecordCount,
        WorkloadDistribution distribution,
        double readPercent = 100.0,
        int seed = 1337)
    {
        var list = new List<WorkloadRequest>(totalRequests);
        Random rng = new(seed);

        long maxKey = Math.Max(1, datasetRecordCount - 1);
        long hotSetSize = Math.Max(100, datasetRecordCount / 100); // 1% hot set

        ulong sequentialCursor = 0;

        for (int i = 0; i < totalRequests; i++)
        {
            ulong key;
            switch (distribution)
            {
                case WorkloadDistribution.Sequential:
                    key = sequentialCursor++ % (ulong)datasetRecordCount;
                    break;

                case WorkloadDistribution.ZipfSkewed:
                    // 85% hot set, 15% cold set
                    if (rng.NextDouble() < 0.85)
                    {
                        key = (ulong)rng.NextInt64(0, hotSetSize);
                    }
                    else
                    {
                        key = (ulong)rng.NextInt64(hotSetSize, datasetRecordCount);
                    }
                    break;

                case WorkloadDistribution.WorkingSetFit:
                    // 20,000 keys fits comfortably in RAM
                    key = (ulong)rng.NextInt64(0, Math.Min(20_000, datasetRecordCount));
                    break;

                case WorkloadDistribution.WorkingSet2xRAM:
                    // 1,000,000 keys (~88 MB payload, exceeds 64MB budget)
                    key = (ulong)rng.NextInt64(0, Math.Min(1_000_000, datasetRecordCount));
                    break;

                case WorkloadDistribution.WorkingSet10xRAM:
                    // 10,000,000 keys
                    key = (ulong)rng.NextInt64(0, Math.Min(10_000_000, datasetRecordCount));
                    break;

                case WorkloadDistribution.Bursty:
                    // Cluster 100 requests on a random hot key
                    ulong clusterBase = (ulong)rng.NextInt64(0, Math.Max(1, datasetRecordCount - 50));
                    key = clusterBase + (ulong)(i % 50);
                    break;

                case WorkloadDistribution.Adversarial:
                    // Stride through the dataset to defeat single-line or sequential prefetching
                    key = (ulong)((i * 104729L) % datasetRecordCount);
                    break;

                case WorkloadDistribution.Uniform:
                default:
                    key = (ulong)rng.NextInt64(0, datasetRecordCount);
                    break;
            }

            bool isRead = rng.NextDouble() * 100.0 < readPercent;
            if (isRead)
            {
                // Select read operation mix: 40% ExactGet, 30% FieldProjection, 20% ComputeDerived, 10% EvaluatePredicate
                double opRoll = rng.NextDouble();
                OperationType opType = opRoll switch
                {
                    < 0.40 => OperationType.ExactGet,
                    < 0.70 => OperationType.FieldProjection,
                    < 0.90 => OperationType.ComputeDerived,
                    _ => OperationType.EvaluatePredicate
                };

                list.Add(new WorkloadRequest
                {
                    OpType = opType,
                    Key = key,
                    Param = (ulong)rng.NextInt64(1, 100_000)
                });
            }
            else
            {
                // Write mutation: 80% update, 20% invalidate
                if (rng.NextDouble() < 0.8)
                {
                    var updatedRecord = new DataRecord
                    {
                        Key = key,
                        TenantId = (uint)(key % 1024),
                        Version = 2,
                        Field1 = (ulong)rng.NextInt64(),
                        Field2 = (ulong)rng.NextInt64(),
                        TimestampTicks = DateTime.UtcNow.Ticks,
                        Payload = new byte[32]
                    };
                    rng.NextBytes(updatedRecord.Payload);

                    list.Add(new WorkloadRequest
                    {
                        OpType = OperationType.Upsert,
                        Key = key,
                        MutationRecord = updatedRecord
                    });
                }
                else
                {
                    list.Add(new WorkloadRequest
                    {
                        OpType = OperationType.Invalidate,
                        Key = key
                    });
                }
            }
        }

        return list;
    }
}
