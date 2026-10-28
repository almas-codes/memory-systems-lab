using System;
using System.IO;
using System.Threading.Tasks;
using NeuroCache.Core;
using NeuroCache.Storage;
using Xunit;

namespace NeuroCache.RecoveryTests;

public class StorageCrashRecoveryTests
{
    [Fact]
    public async Task InterruptedAppend_CorruptedTailTruncatedAndIntegrityRestored()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "neurocache_recovery_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var store = new ExtentStore(tempDir, 10_000);
            await store.InitializeAsync(openForReading: false, enableMMap: false);

            // Write 50 clean records
            for (ulong k = 0; k < 50; k++)
            {
                var rec = new DataRecord
                {
                    Key = k,
                    TenantId = 1,
                    Version = 1,
                    Field1 = k * 10,
                    Field2 = k * 20,
                    TimestampTicks = DateTime.UtcNow.Ticks,
                    Payload = new byte[] { 1, 2, 3, 4 }
                };
                await store.AppendRecordAsync(rec);
            }

            await store.FlushAsync();
            await store.DisposeAsync();

            string segment0 = Path.Combine(tempDir, "segment_00000.dat");
            string indexFile = Path.Combine(tempDir, "extents.idx");

            // Simulate crash: write half a record (corrupt trailing bytes) to segment0
            using (var fs = new FileStream(segment0, FileMode.Append, FileAccess.Write))
            {
                byte[] junk = new byte[] { 0x4E, 0x43, 0x52, 0x43, 0xFF, 0xFF, 0x00 }; // incomplete header
                fs.Write(junk, 0, junk.Length);
                fs.Flush();
            }

            // Run recovery engine
            var recovery = new JournalRecoveryEngine(tempDir);
            var result = recovery.RecoverExtentAndIndex(segment0, indexFile);

            Assert.Equal(RecoveryStatus.Clean, result.Status);
            Assert.True(result.CorruptedTailDetected, "Corrupted tail should be detected");
            Assert.Equal(50, result.ValidRecordsCount);
            Assert.Equal(7, result.BytesTruncated);

            // Re-open extent store and verify clean read
            var reStore = new ExtentStore(tempDir, 10_000);
            await reStore.InitializeAsync(openForReading: true, enableMMap: false);

            for (ulong k = 0; k < 50; k++)
            {
                Assert.True(reStore.TryLookupIndex(k, out var entry));
                byte[] buf = new byte[entry.Length];
                int read = reStore.ReadRandom(entry.SegmentIndex, entry.Offset, buf);
                Assert.Equal(entry.Length, read);
                var rec = DataRecord.ReadFrom(buf);
                Assert.Equal(k, rec.Key);
            }

            await reStore.DisposeAsync();
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
