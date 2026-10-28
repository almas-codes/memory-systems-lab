using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.Core;

namespace NeuroCache.Storage;

public sealed class JournalRecoveryEngine
{
    private readonly string _baseDirectory;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct JournalEntry
    {
        public const uint JournalMagic = 0x4A524E4C; // 'JRNL'

        public uint Magic;
        public ulong TxId;
        public ulong Key;
        public uint Operation; // 1 = Append, 2 = Commit, 3 = Invalidate
        public long TargetOffset;
        public int Length;
        public ulong Checksum;

        public const int Size = 40;
    }

    public JournalRecoveryEngine(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public string JournalPath => Path.Combine(_baseDirectory, "wal.journal");

    public async Task<long> LogIntentAsync(ulong txId, ulong key, uint op, long targetOffset, int length, CancellationToken ct = default)
    {
        JournalEntry entry = new()
        {
            Magic = JournalEntry.JournalMagic,
            TxId = txId,
            Key = key,
            Operation = op,
            TargetOffset = targetOffset,
            Length = length,
            Checksum = 0
        };

        byte[] buf = new byte[JournalEntry.Size];
        MemoryMarshal.Write(buf.AsSpan(0, JournalEntry.Size), in entry);
        ulong cs = System.IO.Hashing.XxHash3.HashToUInt64(buf.AsSpan(0, JournalEntry.Size - 8));
        entry.Checksum = cs;
        MemoryMarshal.Write(buf.AsSpan(0, JournalEntry.Size), in entry);

        using var fs = new FileStream(JournalPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
        await fs.WriteAsync(buf.AsMemory(0, JournalEntry.Size), ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
        return fs.Position;
    }

    public RecoveryResult RecoverExtentAndIndex(string extentFilePath, string indexFilePath)
    {
        var result = new RecoveryResult();

        if (!File.Exists(extentFilePath))
        {
            result.Status = RecoveryStatus.NoFilesToRecover;
            return result;
        }

        // Validate extent file record by record to verify integrity and truncate half-written tail
        using (var fs = new FileStream(extentFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            long validLength = 0;
            byte[] headerBuf = new byte[RecordHeader.HeaderSize];

            while (fs.Position + RecordHeader.HeaderSize <= fs.Length)
            {
                long recordStart = fs.Position;
                fs.ReadExactly(headerBuf, 0, RecordHeader.HeaderSize);

                RecordHeader header = MemoryMarshal.Read<RecordHeader>(headerBuf);
                if (header.Magic != RecordHeader.MagicValue)
                {
                    result.CorruptedTailDetected = true;
                    break;
                }

                if (fs.Position + header.PayloadLength > fs.Length)
                {
                    result.CorruptedTailDetected = true;
                    break;
                }

                byte[] payload = new byte[header.PayloadLength];
                fs.ReadExactly(payload, 0, (int)header.PayloadLength);

                ulong computedChecksum = System.IO.Hashing.XxHash3.HashToUInt64(payload);
                if (computedChecksum != header.Checksum)
                {
                    result.CorruptedTailDetected = true;
                    break;
                }

                validLength = fs.Position;
                result.ValidRecordsCount++;
            }

            if (validLength < fs.Length)
            {
                result.CorruptedTailDetected = true;
                result.BytesTruncated = fs.Length - validLength;
                fs.SetLength(validLength);
                fs.Flush(flushToDisk: true);
            }
        }

        result.Status = RecoveryStatus.Clean;
        return result;
    }
}

public enum RecoveryStatus
{
    Clean,
    Repaired,
    NoFilesToRecover,
    Unrecoverable
}

public sealed class RecoveryResult
{
    public RecoveryStatus Status { get; set; } = RecoveryStatus.Clean;
    public long ValidRecordsCount { get; set; }
    public bool CorruptedTailDetected { get; set; }
    public long BytesTruncated { get; set; }
}
