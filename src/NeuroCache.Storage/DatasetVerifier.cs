using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;

namespace NeuroCache.Storage;

public sealed class VerificationReport
{
    public bool IsValid { get; set; }
    public long DeclaredRecordCount { get; set; }
    public long VerifiedIndexEntries { get; set; }
    public int VerifiedSamplesCount { get; set; }
    public int CorruptionsFound { get; set; }
    public string Message { get; set; } = "";
    public TimeSpan ElapsedTime { get; set; }
}

public sealed class DatasetVerifier
{
    public static async Task<VerificationReport> VerifyAsync(string dataDirectory, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var report = new VerificationReport();

        string manifestPath = Path.Combine(dataDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            report.IsValid = false;
            report.Message = "manifest.json not found in " + dataDirectory;
            return report;
        }

        RecordManifest manifest = RecordManifest.Load(manifestPath);
        report.DeclaredRecordCount = manifest.RecordCount;

        string indexPath = Path.Combine(dataDirectory, "extents.idx");
        if (!File.Exists(indexPath))
        {
            report.IsValid = false;
            report.Message = "extents.idx not found in " + dataDirectory;
            return report;
        }

        FileInfo indexInfo = new(indexPath);
        long expectedIndexSize = manifest.RecordCount * ExtentStore.IndexEntry.Size;
        if (indexInfo.Length != expectedIndexSize)
        {
            report.IsValid = false;
            report.Message = $"Index size mismatch! Expected: {expectedIndexSize} bytes, Found: {indexInfo.Length} bytes";
            return report;
        }

        report.VerifiedIndexEntries = manifest.RecordCount;

        // Initialize extent store to test random record reading
        await using var store = new ExtentStore(dataDirectory, manifest.RecordsPerSegment);
        await store.InitializeAsync(openForReading: true, enableMMap: false, ct).ConfigureAwait(false);

        // Verification sample keys:
        var sampleKeys = new List<ulong>
        {
            0,
            1,
            2,
            (ulong)(manifest.RecordCount / 4),
            (ulong)(manifest.RecordCount / 2),
            (ulong)(3 * manifest.RecordCount / 4),
            (ulong)(manifest.RecordCount - 1)
        };

        // Add 1,000 deterministic pseudo-random keys across the span
        Random rng = new(manifest.GenerationSeed);
        for (int i = 0; i < 1000; i++)
        {
            ulong k = (ulong)rng.NextInt64(0, manifest.RecordCount);
            sampleKeys.Add(k);
        }

        byte[] recordBuf = new byte[RecordHeader.HeaderSize + (manifest.Shape == RecordShape.Large ? 2048 : 256)];

        foreach (ulong key in sampleKeys)
        {
            ct.ThrowIfCancellationRequested();

            if (!store.TryLookupIndex(key, out var indexEntry))
            {
                report.CorruptionsFound++;
                report.Message = $"Failed to find index entry for key {key}";
                report.IsValid = false;
                return report;
            }

            if (indexEntry.Key != key)
            {
                report.CorruptionsFound++;
                report.Message = $"Index entry key mismatch: expected {key}, got {indexEntry.Key}";
                report.IsValid = false;
                return report;
            }

            int read = store.ReadRandom(indexEntry.SegmentIndex, indexEntry.Offset, recordBuf.AsSpan(0, indexEntry.Length));
            if (read < indexEntry.Length)
            {
                report.CorruptionsFound++;
                report.Message = $"Short read on segment {indexEntry.SegmentIndex} at offset {indexEntry.Offset}";
                report.IsValid = false;
                return report;
            }

            RecordHeader header = MemoryMarshal.Read<RecordHeader>(recordBuf.AsSpan(0, RecordHeader.HeaderSize));
            if (header.Magic != RecordHeader.MagicValue || header.Key != key)
            {
                report.CorruptionsFound++;
                report.Message = $"Corrupt record header at key {key}";
                report.IsValid = false;
                return report;
            }

            // Verify payload checksum
            Span<byte> payload = recordBuf.AsSpan(RecordHeader.HeaderSize, (int)header.PayloadLength);
            ulong computedChecksum = System.IO.Hashing.XxHash3.HashToUInt64(payload);
            if (computedChecksum != header.Checksum)
            {
                report.CorruptionsFound++;
                report.Message = $"Checksum mismatch on key {key}";
                report.IsValid = false;
                return report;
            }

            // Verify deterministic fields
            (ulong expF1, ulong expF2) = DatasetGenerator.GenerateFields(manifest.GenerationSeed, key);
            if (header.Field1 != expF1 || header.Field2 != expF2)
            {
                report.CorruptionsFound++;
                report.Message = $"Field values mismatch on key {key}";
                report.IsValid = false;
                return report;
            }

            report.VerifiedSamplesCount++;
        }

        stopwatch.Stop();
        report.IsValid = (report.CorruptionsFound == 0);
        report.ElapsedTime = stopwatch.Elapsed;
        report.Message = $"Successfully verified {report.VerifiedSamplesCount} samples across {manifest.RecordCount:N0} records. All checksums valid.";
        return report;
    }
}
