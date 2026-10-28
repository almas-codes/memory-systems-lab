using System;
using System.IO;
using System.Threading.Tasks;
using NeuroCache.Benchmarks;
using NeuroCache.Core;
using NeuroCache.Storage;
using NeuroCache.Strategies;
using Xunit;

namespace NeuroCache.UnitTests;

public class CoreAndStorageUnitTests
{
    [Fact]
    public void DataRecord_SerializationAndDeserialization_MatchesExactly()
    {
        var record = new DataRecord
        {
            Key = 123456789UL,
            TenantId = 42,
            Version = 3,
            Field1 = 0xDEADBEEFCAFEBABEUL,
            Field2 = 0x0123456789ABCDEFUL,
            TimestampTicks = DateTime.UtcNow.Ticks,
            Payload = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 }
        };

        byte[] buffer = new byte[record.TotalSerializedSize];
        record.WriteTo(buffer);

        var restored = DataRecord.ReadFrom(buffer);

        Assert.Equal(record.Key, restored.Key);
        Assert.Equal(record.TenantId, restored.TenantId);
        Assert.Equal(record.Version, restored.Version);
        Assert.Equal(record.Field1, restored.Field1);
        Assert.Equal(record.Field2, restored.Field2);
        Assert.Equal(record.TimestampTicks, restored.TimestampTicks);
        Assert.Equal(record.Payload, restored.Payload);
    }

    [Fact]
    public void DatasetGenerator_FieldsAreDeterministic()
    {
        int seed = 42;
        ulong key = 987654321UL;

        (ulong f1a, ulong f2a) = DatasetGenerator.GenerateFields(seed, key);
        (ulong f1b, ulong f2b) = DatasetGenerator.GenerateFields(seed, key);

        Assert.Equal(f1a, f1b);
        Assert.Equal(f2a, f2b);
    }

    [Fact]
    public void CostModelSelector_PrioritizesRecomputeOverDisk()
    {
        var selector = new CostModelSelector();

        var route = selector.SelectOptimalRoute(
            operation: OperationType.ComputeDerived,
            hasInlineAnswer: false,
            hasRecomputeSeed: true,
            hasCapsule: true,
            isWitnessValid: true,
            currentRamUsageBytes: 10 * 1024 * 1024,
            ramBudgetBytes: 128 * 1024 * 1024);

        Assert.Equal(RoutingAction.Recompute, route);
    }
}
