using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;

namespace NeuroCache.Storage;

public sealed class DatasetGenerator
{
    private static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    public static int GetPayloadSize(RecordShape shape, ulong key)
    {
        return shape switch
        {
            RecordShape.Small => 32,      // 32-byte payload -> total 88 bytes per record
            RecordShape.Medium => 128,    // 128-byte payload -> total 184 bytes
            RecordShape.Large => 1024,    // 1 KiB payload -> total 1080 bytes
            RecordShape.Variable => (int)(16 + (SplitMix64(key) % 1008)), // 16 to 1024 bytes
            _ => 32
        };
    }

    public static (ulong f1, ulong f2) GenerateFields(int seed, ulong key)
    {
        ulong f1 = SplitMix64((ulong)seed ^ (key * 6364136223846793005UL));
        ulong f2 = SplitMix64(f1 ^ 1442695040888963407UL);
        return (f1, f2);
    }

    public static void FillDeterministicPayload(Span<byte> destination, ulong key, ulong f1, ulong f2)
    {
        int written = 0;
        ulong running = f1 ^ f2;
        while (written < destination.Length)
        {
            int remaining = destination.Length - written;
            if (remaining >= 8)
            {
                MemoryMarshal.Write(destination.Slice(written, 8), in running);
                written += 8;
                running = SplitMix64(running + key);
            }
            else
            {
                for (int b = 0; b < remaining; b++)
                {
                    destination[written++] = (byte)(running >> (b * 8));
                }
            }
        }
    }

    public async Task<RecordManifest> GenerateAsync(
        string targetDirectory,
        long recordCount,
        RecordShape shape = RecordShape.Small,
        int seed = 42,
        Action<long, long>? progressCallback = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDirectory);

        // Pre-flight disk space check
        var hostInfo = HostEnvironmentInfo.Capture(targetDirectory);
        int estAvgPayload = shape switch
        {
            RecordShape.Small => 32,
            RecordShape.Medium => 128,
            RecordShape.Large => 1024,
            RecordShape.Variable => 512,
            _ => 32
        };

        long estTotalBytes = recordCount * (RecordHeader.HeaderSize + estAvgPayload + ExtentStore.IndexEntry.Size);
        if (hostInfo.StorageFreeBytes > 0 && hostInfo.StorageFreeBytes < estTotalBytes + 2L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"Insufficient disk space! Estimated required: {estTotalBytes / (1024 * 1024)} MB, Available: {hostInfo.StorageFreeBytes / (1024 * 1024)} MB");
        }

        string indexPath = Path.Combine(targetDirectory, "extents.idx");
        using var indexStream = new FileStream(indexPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);

        int recordsPerSegment = 5_000_000;
        long generated = 0;
        int segmentIndex = 0;
        long totalDataBytes = 0;

        const int batchSize = 200_000;
        int maxRecordSize = RecordHeader.HeaderSize + (shape == RecordShape.Large ? 1024 : shape == RecordShape.Medium ? 128 : shape == RecordShape.Variable ? 1024 : 32);
        byte[] dataBatchBuffer = new byte[batchSize * maxRecordSize];
        byte[] indexBatchBuffer = new byte[batchSize * ExtentStore.IndexEntry.Size];

        FileStream? currentSegmentStream = null;

        try
        {
            while (generated < recordCount)
            {
                ct.ThrowIfCancellationRequested();

                if (currentSegmentStream == null || generated % recordsPerSegment == 0)
                {
                    if (currentSegmentStream != null)
                    {
                        await currentSegmentStream.FlushAsync(ct).ConfigureAwait(false);
                        await currentSegmentStream.DisposeAsync().ConfigureAwait(false);
                        segmentIndex++;
                    }

                    string segmentPath = Path.Combine(targetDirectory, $"segment_{segmentIndex:D5}.dat");
                    currentSegmentStream = new FileStream(segmentPath, FileMode.Create, FileAccess.Write, FileShare.None, 2 * 1024 * 1024, FileOptions.None);
                }

                int currentBatch = (int)Math.Min(batchSize, recordCount - generated);
                long startSegmentOffset = currentSegmentStream.Position;
                int fixedRecordSize = RecordHeader.HeaderSize + estAvgPayload;

                // Parallelize deterministic payload and header generation across cores
                Parallel.For(0, currentBatch, i =>
                {
                    ulong key = (ulong)(generated + i);
                    (ulong f1, ulong f2) = GenerateFields(seed, key);
                    int payloadSize = GetPayloadSize(shape, key);
                    int recordTotalSize = RecordHeader.HeaderSize + payloadSize;

                    int dataOffset = i * fixedRecordSize;
                    Span<byte> recordSpan = dataBatchBuffer.AsSpan(dataOffset, recordTotalSize);
                    Span<byte> payloadSpan = recordSpan.Slice(RecordHeader.HeaderSize, payloadSize);

                    FillDeterministicPayload(payloadSpan, key, f1, f2);
                    ulong payloadChecksum = System.IO.Hashing.XxHash3.HashToUInt64(payloadSpan);

                    RecordHeader header = new()
                    {
                        Magic = RecordHeader.MagicValue,
                        Key = key,
                        TenantId = (uint)(key % 1024),
                        Version = 1,
                        PayloadLength = (uint)payloadSize,
                        Checksum = payloadChecksum,
                        TimestampTicks = DateTime.UtcNow.Ticks,
                        Field1 = f1,
                        Field2 = f2
                    };

                    MemoryMarshal.Write(recordSpan.Slice(0, RecordHeader.HeaderSize), in header);

                    ExtentStore.IndexEntry indexEntry = new()
                    {
                        Key = key,
                        SegmentIndex = segmentIndex,
                        Offset = startSegmentOffset + dataOffset,
                        Length = recordTotalSize
                    };

                    MemoryMarshal.Write(indexBatchBuffer.AsSpan(i * ExtentStore.IndexEntry.Size, ExtentStore.IndexEntry.Size), in indexEntry);
                });

                int totalBatchDataBytes = currentBatch * fixedRecordSize;
                await currentSegmentStream.WriteAsync(dataBatchBuffer.AsMemory(0, totalBatchDataBytes), ct).ConfigureAwait(false);
                await indexStream.WriteAsync(indexBatchBuffer.AsMemory(0, currentBatch * ExtentStore.IndexEntry.Size), ct).ConfigureAwait(false);

                totalDataBytes += totalBatchDataBytes;
                generated += currentBatch;

                if (progressCallback != null && (generated % 5_000_000 == 0 || generated == recordCount))
                {
                    progressCallback(generated, recordCount);
                }
            }

            if (currentSegmentStream != null)
            {
                await currentSegmentStream.FlushAsync(ct).ConfigureAwait(false);
                await currentSegmentStream.DisposeAsync().ConfigureAwait(false);
                currentSegmentStream = null;
                segmentIndex++;
            }

            await indexStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (currentSegmentStream != null)
                await currentSegmentStream.DisposeAsync().ConfigureAwait(false);
        }

        RecordManifest manifest = new()
        {
            RecordCount = recordCount,
            Shape = shape,
            AveragePayloadBytes = estAvgPayload,
            TotalBytesOnDisk = totalDataBytes + (recordCount * ExtentStore.IndexEntry.Size),
            GenerationSeed = seed,
            SchemaVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SegmentCount = segmentIndex,
            RecordsPerSegment = recordsPerSegment,
            DataDirectory = targetDirectory,
            MinKey = 0,
            MaxKey = (ulong)(recordCount - 1)
        };

        string manifestPath = Path.Combine(targetDirectory, "manifest.json");
        manifest.Save(manifestPath);

        return manifest;
    }
}
