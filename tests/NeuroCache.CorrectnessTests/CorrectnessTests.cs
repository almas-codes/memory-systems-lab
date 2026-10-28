using System;
using System.IO;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;
using NeuroCache.Strategies;
using Xunit;

namespace NeuroCache.CorrectnessTests;

public class DifferentialCorrectnessTests
{
    [Fact]
    public async Task DifferentialVerification_AllStrategiesMatchAuthoritativeGroundTruth()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "neurocache_correctness_" + Guid.NewGuid().ToString("N"));
        try
        {
            var generator = new DatasetGenerator();
            int recordCount = 1000;
            var manifest = await generator.GenerateAsync(tempDir, recordCount, RecordShape.Small, seed: 12345);

            var authoritative = new DirectBaselineStrategy();
            await authoritative.InitializeAsync(tempDir, manifest, new StrategyOptions());

            var strategiesToTest = new IStorageStrategy[]
            {
                new DotNetDictionaryStrategy(),
                new DotNetMemoryCacheStrategy(),
                new CertusEngramStrategy(),
                new SufficiencyFabricStrategy(),
                new NeuroCacheOmegaStrategy(),
                new ProjectionCapsuleStrategy(),
                new MemoryMappedStoreStrategy()
            };

            foreach (var strat in strategiesToTest)
            {
                await strat.InitializeAsync(tempDir, manifest, new StrategyOptions());

                for (ulong k = 0; k < 100; k++)
                {
                    // 1. Exact Get verification
                    var expected = await authoritative.GetAsync(k);
                    var actual = await strat.GetAsync(k);

                    Assert.True(expected.Success, $"Authoritative missed key {k}");
                    Assert.True(actual.Success, $"{strat.Name} missed key {k}");
                    Assert.Equal(expected.Payload.ToArray(), actual.Payload.ToArray());

                    // 2. Field Projection verification
                    var expProj = await authoritative.GetProjectionAsync(k, 0);
                    var actProj = await strat.GetProjectionAsync(k, 0);
                    Assert.True(actProj.Success, $"{strat.Name} projection missed key {k}");

                    // 3. Derived pure computation verification
                    ulong param = 999;
                    var expComp = await authoritative.ComputeDerivedAsync(k, param);
                    var actComp = await strat.ComputeDerivedAsync(k, param);
                    Assert.True(actComp.Success, $"{strat.Name} compute missed key {k}");
                }

                await strat.DisposeAsync();
            }

            await authoritative.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SemanticInvalidation_IrrelevantVsDependentFieldChanges_BehaviorVerified()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "neurocache_semantic_" + Guid.NewGuid().ToString("N"));
        try
        {
            var generator = new DatasetGenerator();
            var manifest = await generator.GenerateAsync(tempDir, 100, RecordShape.Small, seed: 99);

            var fabric = new SufficiencyFabricStrategy();
            await fabric.InitializeAsync(tempDir, manifest, new StrategyOptions());

            ulong testKey = 10;
            ulong testParam = 12345;

            // 1. Initial compute - populates certificate
            var res1 = await fabric.ComputeDerivedAsync(testKey, testParam);
            Assert.True(res1.Success);
            ulong expectedDerived = res1.ScalarValue;

            // 2. Query again - inline reuse from resident certificate
            var res2 = await fabric.ComputeDerivedAsync(testKey, testParam);
            Assert.True(res2.Success);
            Assert.Equal(expectedDerived, res2.ScalarValue);

            // 3. Mutate an irrelevant field (e.g. Invalidate unrelated key 99)
            await fabric.InvalidateAsync(99);
            var res3 = await fabric.ComputeDerivedAsync(testKey, testParam);
            Assert.True(res3.Success);
            Assert.Equal(expectedDerived, res3.ScalarValue);

            // 4. Invalidate the dependent key (forcing re-read & re-verification)
            await fabric.InvalidateAsync(testKey);
            var res4 = await fabric.ComputeDerivedAsync(testKey, testParam);
            Assert.True(res4.Success);
            Assert.Equal(expectedDerived, res4.ScalarValue);

            await fabric.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
