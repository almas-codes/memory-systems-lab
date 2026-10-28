using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeuroCache.Core;

public enum RecordShape : byte
{
    Small = 1,       // 32-48 bytes total (e.g., 32-byte payload)
    Medium = 2,      // 128-256 bytes total
    Large = 3,       // 1024-2048 bytes total
    Variable = 4     // Mixture of small, medium, large
}

public enum OperationType : byte
{
    ExactGet = 1,
    FieldProjection = 2,
    ComputeDerived = 3,
    EvaluatePredicate = 4,
    AggregateRange = 5,
    Upsert = 6,
    Invalidate = 7
}

public enum ResultSource : byte
{
    NotFound = 0,
    InlineReuse = 1,
    Recompute = 2,
    CapsuleRead = 3,
    ExtentRead = 4,
    MemoryMappedRead = 5,
    AuthoritativeFallback = 6
}

public readonly record struct StrategyResult(
    bool Success,
    bool IsHit,
    ResultSource Source,
    int BytesRead,
    ulong ScalarValue,
    ReadOnlyMemory<byte> Payload)
{
    public static StrategyResult Miss => new(false, false, ResultSource.NotFound, 0, 0, ReadOnlyMemory<byte>.Empty);
    public static StrategyResult FromInline(ulong scalar) => new(true, true, ResultSource.InlineReuse, 0, scalar, ReadOnlyMemory<byte>.Empty);
    public static StrategyResult FromInline(ReadOnlyMemory<byte> payload) => new(true, true, ResultSource.InlineReuse, 0, 0, payload);
    public static StrategyResult FromRecompute(ulong scalar) => new(true, true, ResultSource.Recompute, 0, scalar, ReadOnlyMemory<byte>.Empty);
    public static StrategyResult FromCapsule(ReadOnlyMemory<byte> payload, int bytesRead) => new(true, true, ResultSource.CapsuleRead, bytesRead, 0, payload);
    public static StrategyResult FromCapsule(ulong scalar, int bytesRead) => new(true, true, ResultSource.CapsuleRead, bytesRead, scalar, ReadOnlyMemory<byte>.Empty);
    public static StrategyResult FromExtent(ReadOnlyMemory<byte> payload, int bytesRead) => new(true, true, ResultSource.ExtentRead, bytesRead, 0, payload);
    public static StrategyResult FromExtent(ulong scalar, int bytesRead) => new(true, true, ResultSource.ExtentRead, bytesRead, scalar, ReadOnlyMemory<byte>.Empty);
    public static StrategyResult FromMMap(ReadOnlyMemory<byte> payload) => new(true, true, ResultSource.MemoryMappedRead, payload.Length, 0, payload);
    public static StrategyResult FromFallback(ReadOnlyMemory<byte> payload, int bytesRead) => new(true, false, ResultSource.AuthoritativeFallback, bytesRead, 0, payload);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RecordHeader
{
    public const uint MagicValue = 0x4E435243; // 'NCRC'

    public uint Magic;            // 4 bytes
    public ulong Key;             // 8 bytes
    public uint TenantId;         // 4 bytes (State Witness domain)
    public uint Version;          // 4 bytes (record epoch)
    public uint PayloadLength;    // 4 bytes
    public ulong Checksum;        // 8 bytes (XxHash3 of payload)
    public long TimestampTicks;   // 8 bytes
    public ulong Field1;          // 8 bytes (fast scalar for projections)
    public ulong Field2;          // 8 bytes (fast scalar for projections)

    public const int HeaderSize = 56;
}

public sealed class DataRecord
{
    public ulong Key { get; set; }
    public uint TenantId { get; set; }
    public uint Version { get; set; }
    public ulong Field1 { get; set; }
    public ulong Field2 { get; set; }
    public long TimestampTicks { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public ulong CalculateChecksum()
    {
        return System.IO.Hashing.XxHash3.HashToUInt64(Payload);
    }

    public int TotalSerializedSize => RecordHeader.HeaderSize + Payload.Length;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < TotalSerializedSize)
            throw new ArgumentException("Destination span too small", nameof(destination));

        RecordHeader header = new()
        {
            Magic = RecordHeader.MagicValue,
            Key = Key,
            TenantId = TenantId,
            Version = Version,
            PayloadLength = (uint)Payload.Length,
            Checksum = CalculateChecksum(),
            TimestampTicks = TimestampTicks,
            Field1 = Field1,
            Field2 = Field2
        };

        MemoryMarshal.Write(destination, in header);
        Payload.AsSpan().CopyTo(destination.Slice(RecordHeader.HeaderSize));
    }

    public static DataRecord ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < RecordHeader.HeaderSize)
            throw new ArgumentException("Source span smaller than header", nameof(source));

        RecordHeader header = MemoryMarshal.Read<RecordHeader>(source);
        if (header.Magic != RecordHeader.MagicValue)
            throw new InvalidDataException($"Invalid record header magic: 0x{header.Magic:X8}");

        if (source.Length < RecordHeader.HeaderSize + header.PayloadLength)
            throw new InvalidDataException("Source span smaller than declared payload length");

        byte[] payload = source.Slice(RecordHeader.HeaderSize, (int)header.PayloadLength).ToArray();
        ulong computedChecksum = System.IO.Hashing.XxHash3.HashToUInt64(payload);
        if (computedChecksum != header.Checksum)
            throw new InvalidDataException($"Checksum mismatch for record {header.Key}: expected 0x{header.Checksum:X16}, got 0x{computedChecksum:X16}");

        return new DataRecord
        {
            Key = header.Key,
            TenantId = header.TenantId,
            Version = header.Version,
            Field1 = header.Field1,
            Field2 = header.Field2,
            TimestampTicks = header.TimestampTicks,
            Payload = payload
        };
    }
}

public sealed class RecordManifest
{
    public long RecordCount { get; set; }
    public RecordShape Shape { get; set; }
    public int AveragePayloadBytes { get; set; }
    public long TotalBytesOnDisk { get; set; }
    public int GenerationSeed { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public ulong DataChecksum { get; set; }
    public int SegmentCount { get; set; }
    public int RecordsPerSegment { get; set; }
    public string DataDirectory { get; set; } = "";
    public ulong MinKey { get; set; }
    public ulong MaxKey { get; set; }

    public void Save(string filePath)
    {
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    public static RecordManifest Load(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<RecordManifest>(json)
            ?? throw new InvalidOperationException("Failed to deserialize dataset manifest");
    }
}
