using System;
using System.IO;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;
using NeuroCache.Strategies;
using Xunit;

namespace NeuroCache.ScaleTests;

public class HighVolumeScaleTests
{
    [Fact]
    public async Task StreamingDatasetGeneration_MaintainsBoundedHeapMemory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "neurocache_scale_" + Guid.NewGuid().ToString("N"));
        try
        {
            var generator = new DatasetGenerator();
            long initialMemory = GC.GetTotalMemory(true);

            // Generate 50,000 records in streaming batches
            var manifest = await generator.GenerateAsync(tempDir, 50_000, RecordShape.Small, seed: 777);

            long finalMemory = GC.GetTotalMemory(true);
            long memoryDelta = finalMemory - initialMemory;

            Assert.Equal(50_000, manifest.RecordCount);

            // Verify heap didn't blow up (should be bounded well under 50MB)
            Assert.True(memoryDelta < 50L * 1024 * 1024, $"Memory delta {memoryDelta / (1024 * 1024)} MB exceeded bounded limit");

            var verifyReport = await DatasetVerifier.VerifyAsync(tempDir);
            Assert.True(verifyReport.IsValid, verifyReport.Message);
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
    public async Task StrictRamBudget_DoesNotExceedAllocatedLimit()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "neurocache_budget_" + Guid.NewGuid().ToString("N"));
        try
        {
            var generator = new DatasetGenerator();
            var manifest = await generator.GenerateAsync(tempDir, 10_000, RecordShape.Small, seed: 999);

            long budgetBytes = 512 * 1024; // 512 KB tight budget
            var options = new StrategyOptions { RamBudgetBytes = budgetBytes };

            var dictStrategy = new DotNetDictionaryStrategy();
            await dictStrategy.InitializeAsync(tempDir, manifest, options);

            // Attempt to query all 10,000 items
            for (ulong k = 0; k < 10_000; k++)
            {
                await dictStrategy.GetAsync(k);
            }

            var stats = dictStrategy.GetStats();
            // Resident bytes must be less than or equal to budgetBytes
            Assert.True(stats.ResidentBytes <= budgetBytes, $"Resident bytes {stats.ResidentBytes} exceeded budget {budgetBytes}");

            await dictStrategy.DisposeAsync();
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
