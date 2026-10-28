using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NeuroCache.Core;

namespace NeuroCache.Storage;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CapsuleHeader
{
    public const uint CapsuleMagic = 0x50524F4A; // 'PROJ'

    public uint Magic;              // 4 bytes
    public ulong Key;               // 8 bytes
    public uint OperationRev;       // 4 bytes
    public uint WitnessDomainId;    // 4 bytes
    public uint WitnessGeneration;  // 4 bytes
    public ulong ProjectedField1;   // 8 bytes
    public ulong ProjectedField2;   // 8 bytes
    public ulong PureHash;          // 8 bytes
    public uint ResultLength;       // 4 bytes
    public ulong Checksum;          // 8 bytes

    public const int HeaderSize = 60;
}

public sealed class CapsuleStore : IAsyncDisposable
{
    private readonly string _capsuleDirectory;
    private FileStream? _capsuleStream;
    private SafeFileHandle? _capsuleHandle;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CapsuleStore(string baseDirectory)
    {
        _capsuleDirectory = Path.Combine(baseDirectory, "capsules");
        Directory.CreateDirectory(_capsuleDirectory);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        string filePath = Path.Combine(_capsuleDirectory, "projection_capsules.dat");
        _capsuleStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 64 * 1024, FileOptions.RandomAccess);
        _capsuleHandle = _capsuleStream.SafeFileHandle;
    }

    public static int CalculateCapsuleSize(int resultLength) => CapsuleHeader.HeaderSize + resultLength;

    public async ValueTask<long> WriteCapsuleAsync(
        ulong key,
        uint opRev,
        uint witnessDomainId,
        uint witnessGen,
        ulong field1,
        ulong field2,
        ulong pureHash,
        ReadOnlyMemory<byte> resultPayload,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_capsuleStream == null)
                throw new InvalidOperationException("CapsuleStore not initialized");

            int totalSize = CalculateCapsuleSize(resultPayload.Length);
            byte[] buf = ArrayPool<byte>.Shared.Rent(totalSize);
            long offset;

            try
            {
                CapsuleHeader header = new()
                {
                    Magic = CapsuleHeader.CapsuleMagic,
                    Key = key,
                    OperationRev = opRev,
                    WitnessDomainId = witnessDomainId,
                    WitnessGeneration = witnessGen,
                    ProjectedField1 = field1,
                    ProjectedField2 = field2,
                    PureHash = pureHash,
                    ResultLength = (uint)resultPayload.Length,
                    Checksum = resultPayload.IsEmpty ? 0 : System.IO.Hashing.XxHash3.HashToUInt64(resultPayload.Span)
                };

                MemoryMarshal.Write(buf.AsSpan(0, CapsuleHeader.HeaderSize), in header);
                if (!resultPayload.IsEmpty)
                {
                    resultPayload.Span.CopyTo(buf.AsSpan(CapsuleHeader.HeaderSize, resultPayload.Length));
                }

                // If keyed by slot directly or appended:
                // For direct single-read access, write at fixed slot offset if fixed size, or append
                offset = _capsuleStream.Length;
                _capsuleStream.Seek(offset, SeekOrigin.Begin);
                await _capsuleStream.WriteAsync(buf.AsMemory(0, totalSize), ct).ConfigureAwait(false);
                return offset;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool TryReadCapsule(
        long offset,
        Span<byte> destination,
        out CapsuleHeader header,
        out int totalBytesRead)
    {
        header = default;
        totalBytesRead = 0;
        if (_capsuleHandle == null) return false;

        Span<byte> headerBuf = stackalloc byte[CapsuleHeader.HeaderSize];
        int read = RandomAccess.Read(_capsuleHandle, headerBuf, offset);
        if (read < CapsuleHeader.HeaderSize)
            return false;

        header = MemoryMarshal.Read<CapsuleHeader>(headerBuf);
        if (header.Magic != CapsuleHeader.CapsuleMagic)
            return false;

        totalBytesRead = CapsuleHeader.HeaderSize + (int)header.ResultLength;

        if (header.ResultLength > 0)
        {
            if (destination.Length < (int)header.ResultLength)
                return false;

            int payloadRead = RandomAccess.Read(_capsuleHandle, destination.Slice(0, (int)header.ResultLength), offset + CapsuleHeader.HeaderSize);
            if (payloadRead < (int)header.ResultLength)
                return false;

            ulong check = System.IO.Hashing.XxHash3.HashToUInt64(destination.Slice(0, (int)header.ResultLength));
            if (check != header.Checksum)
                return false;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_capsuleStream != null)
        {
            await _capsuleStream.FlushAsync().ConfigureAwait(false);
            await _capsuleStream.DisposeAsync().ConfigureAwait(false);
            _capsuleStream = null;
        }
    }
}
