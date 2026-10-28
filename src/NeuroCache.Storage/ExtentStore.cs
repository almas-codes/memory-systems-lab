using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NeuroCache.Core;

namespace NeuroCache.Storage;

public sealed class ExtentStore : IAsyncDisposable
{
    private readonly string _baseDirectory;
    private readonly int _recordsPerSegment;
    private readonly List<FileStream> _segmentStreams = new();
    private readonly List<SafeFileHandle> _segmentHandles = new();
    private readonly List<MemoryMappedFile?> _mmapFiles = new();
    private readonly List<MemoryMappedViewAccessor?> _mmapAccessors = new();
    private FileStream? _activeIndexStream;
    private SafeFileHandle? _activeIndexHandle;
    private MemoryMappedFile? _mmapIndex;
    private MemoryMappedViewAccessor? _mmapIndexAccessor;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IndexEntry
    {
        public ulong Key;
        public int SegmentIndex;
        public long Offset;
        public int Length;

        public const int Size = 24;
    }

    public ExtentStore(string baseDirectory, int recordsPerSegment = 5_000_000)
    {
        _baseDirectory = baseDirectory;
        _recordsPerSegment = recordsPerSegment;
        Directory.CreateDirectory(_baseDirectory);
    }

    public async Task InitializeAsync(bool openForReading = true, bool enableMMap = false, CancellationToken ct = default)
    {
        string indexPath = Path.Combine(_baseDirectory, "extents.idx");
        var fileAccess = FileAccess.ReadWrite;
        var fileShare = FileShare.ReadWrite;
        var fileMode = FileMode.OpenOrCreate;

        _activeIndexStream = new FileStream(indexPath, fileMode, fileAccess, fileShare, 64 * 1024, FileOptions.RandomAccess);
        _activeIndexHandle = _activeIndexStream.SafeFileHandle;

        // Discover existing segments
        string[] segmentFiles = Directory.GetFiles(_baseDirectory, "segment_*.dat");
        Array.Sort(segmentFiles);

        foreach (string file in segmentFiles)
        {
            var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 64 * 1024, FileOptions.RandomAccess);
            _segmentStreams.Add(stream);
            _segmentHandles.Add(stream.SafeFileHandle);

            if (enableMMap && stream.Length > 0)
            {
                var mmf = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                _mmapFiles.Add(mmf);
                _mmapAccessors.Add(mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read));
            }
            else
            {
                _mmapFiles.Add(null);
                _mmapAccessors.Add(null);
            }
        }

        if (enableMMap && _activeIndexStream.Length > 0)
        {
            try
            {
                _mmapIndex = MemoryMappedFile.CreateFromFile(_activeIndexStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                _mmapIndexAccessor = _mmapIndex.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }
            catch
            {
                // Fall back to direct file read if mmap fails
            }
        }
    }

    public int GetSegmentCount() => _segmentStreams.Count;

    public async ValueTask<(int segmentIndex, long offset, int length)> AppendRecordAsync(DataRecord record, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int segmentIndex = _segmentStreams.Count - 1;
            FileStream currentSegment;

            int recordSize = record.TotalSerializedSize;

            if (segmentIndex < 0 || _segmentStreams[segmentIndex].Length > 1024L * 1024 * 512) // 512MB per segment
            {
                segmentIndex = _segmentStreams.Count;
                string newSegmentPath = Path.Combine(_baseDirectory, $"segment_{segmentIndex:D5}.dat");
                currentSegment = new FileStream(newSegmentPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan);
                _segmentStreams.Add(currentSegment);
                _segmentHandles.Add(currentSegment.SafeFileHandle);
                _mmapFiles.Add(null);
                _mmapAccessors.Add(null);
            }
            else
            {
                currentSegment = _segmentStreams[segmentIndex];
            }

            long offset = currentSegment.Position;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(recordSize);
            try
            {
                record.WriteTo(buffer.AsSpan(0, recordSize));
                await currentSegment.WriteAsync(buffer.AsMemory(0, recordSize), ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // Write to index
            IndexEntry entry = new()
            {
                Key = record.Key,
                SegmentIndex = segmentIndex,
                Offset = offset,
                Length = recordSize
            };

            byte[] indexBuf = ArrayPool<byte>.Shared.Rent(IndexEntry.Size);
            try
            {
                MemoryMarshal.Write(indexBuf.AsSpan(0, IndexEntry.Size), in entry);
                if (_activeIndexStream != null)
                {
                    await _activeIndexStream.WriteAsync(indexBuf.AsMemory(0, IndexEntry.Size), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(indexBuf);
            }

            return (segmentIndex, offset, recordSize);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask AppendBatchAsync(ReadOnlyMemory<DataRecord> records, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (records.IsEmpty) return;

            int segmentIndex = _segmentStreams.Count - 1;
            FileStream currentSegment;

            if (segmentIndex < 0)
            {
                segmentIndex = 0;
                string newSegmentPath = Path.Combine(_baseDirectory, $"segment_{segmentIndex:D5}.dat");
                currentSegment = new FileStream(newSegmentPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 1024 * 1024, FileOptions.None);
                _segmentStreams.Add(currentSegment);
                _segmentHandles.Add(currentSegment.SafeFileHandle);
                _mmapFiles.Add(null);
                _mmapAccessors.Add(null);
            }
            else
            {
                currentSegment = _segmentStreams[segmentIndex];
            }

            int count = records.Length;
            long indexBatchBytes = count * (long)IndexEntry.Size;
            byte[] indexChunk = ArrayPool<byte>.Shared.Rent((int)indexBatchBytes);

            int totalDataBytes = 0;
            for (int i = 0; i < count; i++)
            {
                totalDataBytes += records.Span[i].TotalSerializedSize;
            }

            byte[] dataChunk = ArrayPool<byte>.Shared.Rent(totalDataBytes);

            try
            {
                int dataCursor = 0;
                long startOffset = currentSegment.Position;

                for (int i = 0; i < count; i++)
                {
                    var rec = records.Span[i];
                    int recSize = rec.TotalSerializedSize;
                    long recOffset = startOffset + dataCursor;

                    rec.WriteTo(dataChunk.AsSpan(dataCursor, recSize));
                    dataCursor += recSize;

                    IndexEntry entry = new()
                    {
                        Key = rec.Key,
                        SegmentIndex = segmentIndex,
                        Offset = recOffset,
                        Length = recSize
                    };

                    MemoryMarshal.Write(indexChunk.AsSpan(i * IndexEntry.Size, IndexEntry.Size), in entry);
                }

                await currentSegment.WriteAsync(dataChunk.AsMemory(0, totalDataBytes), ct).ConfigureAwait(false);

                if (_activeIndexStream != null)
                {
                    await _activeIndexStream.WriteAsync(indexChunk.AsMemory(0, (int)indexBatchBytes), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(dataChunk);
                ArrayPool<byte>.Shared.Return(indexChunk);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task FlushAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var stream in _segmentStreams)
            {
                await stream.FlushAsync().ConfigureAwait(false);
            }
            if (_activeIndexStream != null)
            {
                await _activeIndexStream.FlushAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool TryReadMMap(int segmentIndex, long offset, int length, Span<byte> destination)
    {
        if (segmentIndex < 0 || segmentIndex >= _mmapAccessors.Count)
            return false;

        var accessor = _mmapAccessors[segmentIndex];
        if (accessor == null)
            return false;

        unsafe
        {
            byte* ptr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try
            {
                new ReadOnlySpan<byte>(ptr + offset, length).CopyTo(destination);
                return true;
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    public int ReadRandom(int segmentIndex, long offset, Span<byte> destination)
    {
        if (segmentIndex < 0 || segmentIndex >= _segmentHandles.Count)
            return 0;

        SafeFileHandle handle = _segmentHandles[segmentIndex];
        return RandomAccess.Read(handle, destination, offset);
    }

    public async ValueTask<int> ReadRandomAsync(int segmentIndex, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        if (segmentIndex < 0 || segmentIndex >= _segmentHandles.Count)
            return 0;

        SafeFileHandle handle = _segmentHandles[segmentIndex];
        return await RandomAccess.ReadAsync(handle, destination, offset, ct).ConfigureAwait(false);
    }

    public bool TryLookupIndex(ulong key, out IndexEntry entry)
    {
        // When key is dense 0-based indexing:
        long indexOffset = (long)key * IndexEntry.Size;

        if (_mmapIndexAccessor != null)
        {
            if (indexOffset + IndexEntry.Size <= _mmapIndexAccessor.Capacity)
            {
                _mmapIndexAccessor.Read(indexOffset, out entry);
                if (entry.Key == key)
                    return true;
            }
        }
        else if (_activeIndexHandle != null)
        {
            Span<byte> buf = stackalloc byte[IndexEntry.Size];
            int read = RandomAccess.Read(_activeIndexHandle, buf, indexOffset);
            if (read == IndexEntry.Size)
            {
                entry = MemoryMarshal.Read<IndexEntry>(buf);
                if (entry.Key == key)
                    return true;
            }
        }

        entry = default;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _mmapIndexAccessor?.Dispose();
        _mmapIndex?.Dispose();

        foreach (var acc in _mmapAccessors)
            acc?.Dispose();
        foreach (var mmf in _mmapFiles)
            mmf?.Dispose();

        if (_activeIndexStream != null)
            await _activeIndexStream.DisposeAsync().ConfigureAwait(false);

        foreach (var stream in _segmentStreams)
            await stream.DisposeAsync().ConfigureAwait(false);

        _segmentStreams.Clear();
        _segmentHandles.Clear();
        _mmapFiles.Clear();
        _mmapAccessors.Clear();
    }
}
