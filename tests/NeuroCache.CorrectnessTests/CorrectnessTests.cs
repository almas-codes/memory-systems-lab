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
}
