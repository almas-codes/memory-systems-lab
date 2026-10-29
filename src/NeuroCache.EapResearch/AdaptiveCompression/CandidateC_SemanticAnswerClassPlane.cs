using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NeuroCache.EapResearch.AdaptiveCompression;

/// <summary>
/// Record of a registered semantic answer class.
/// </summary>
public sealed class SemanticClassInfo
{
    public byte ClassId { get; init; }
    public byte AnswerPattern { get; init; }
    public string Description { get; init; } = "";
    public DateTime RegisteredAtUtc { get; init; }
}

/// <summary>
/// Candidate C: Adaptive Semantic Answer-Class Encoding.
/// Encodes records as compact 4-bit nibbles (2 records per byte) mapped to a semantic answer dictionary.
/// Footprint for 50,000,000 records:
///   Payload: 25,000,000 bytes = 25.00 MB decimal (23.84 MiB binary).
///   Dictionary: 272 bytes.
///   Total: 25.000272 MB decimal (50.0% reduction compared to Candidates A and B).
/// </summary>
public sealed class CandidateC_SemanticAnswerClassPlane
{
    private const int MaxClasses = 16;
    private readonly byte[] _data;
    private readonly long _maxId;

    // Fast dictionary mapping: ClassId (0..15) -> 8-bit AnswerPattern
    private readonly byte[] _classToPattern = new byte[MaxClasses];

    // Fast reverse mapping: 8-bit AnswerPattern (0..255) -> ClassId (0..15) or 0xFF (unregistered)
    private readonly byte[] _patternToClass = new byte[256];

    private readonly object _dictionaryLock = new();
    private int _distinctClassCount;
    private readonly List<SemanticClassInfo> _classRegistry = new();

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _data.LongLength + _classToPattern.Length + _patternToClass.Length;
    public int DistinctClassCount => Volatile.Read(ref _distinctClassCount);
    public IReadOnlyList<SemanticClassInfo> RegisteredClasses
    {
        get
        {
            lock (_dictionaryLock)
            {
                return _classRegistry.ToArray();
            }
        }
    }

    public CandidateC_SemanticAnswerClassPlane(long maxId)
    {
        _maxId = maxId;
        long byteCount = (maxId + 2) / 2;
        _data = new byte[byteCount];

        // Initialize reverse dictionary to 0xFF (unmapped)
        Array.Fill(_patternToClass, (byte)0xFF);

        // Pre-register default empty pattern (0) as class 0
        RegisterPattern(0, "All Queries False");
    }

    /// <summary>
    /// Thread-safe class registration.
    /// Returns the assigned classId for the pattern.
    /// </summary>
    public byte GetOrRegisterPattern(byte pattern, string description = "")
    {
        byte existing = Volatile.Read(ref _patternToClass[pattern]);
        if (existing != 0xFF)
            return existing;

        lock (_dictionaryLock)
        {
            if (_patternToClass[pattern] != 0xFF)
                return _patternToClass[pattern];

            if (_distinctClassCount >= MaxClasses)
            {
                throw new InvalidOperationException(
                    $"Semantic class dictionary overflow: observed {_distinctClassCount + 1} distinct answer classes, exceeding maximum 4-bit nibble capacity ({MaxClasses}).");
            }

            byte classId = (byte)_distinctClassCount;
            _classToPattern[classId] = pattern;
            _patternToClass[pattern] = classId;

            _classRegistry.Add(new SemanticClassInfo
            {
                ClassId = classId,
                AnswerPattern = pattern,
                Description = string.IsNullOrEmpty(description) ? $"Pattern 0x{pattern:X2}" : description,
                RegisteredAtUtc = DateTime.UtcNow
            });

            Interlocked.Increment(ref _distinctClassCount);
            return classId;
        }
    }

    private void RegisterPattern(byte pattern, string description)
    {
        lock (_dictionaryLock)
        {
            byte classId = (byte)_distinctClassCount;
            _classToPattern[classId] = pattern;
            _patternToClass[pattern] = classId;
            _classRegistry.Add(new SemanticClassInfo
            {
                ClassId = classId,
                AnswerPattern = pattern,
                Description = description,
                RegisteredAtUtc = DateTime.UtcNow
            });
            _distinctClassCount++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool QuerySingle(long id, int queryIndex)
    {
        long byteIdx = id >> 1;
        byte b = _data[byteIdx];
        byte classId = (id & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
        byte pattern = _classToPattern[classId];
        return ((pattern >> (7 - queryIndex)) & 1) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryAll(long id)
    {
        long byteIdx = id >> 1;
        byte b = _data[byteIdx];
        byte classId = (id & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
        return _classToPattern[classId];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte QueryClassId(long id)
    {
        long byteIdx = id >> 1;
        byte b = _data[byteIdx];
        return (id & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(long id, int balance, bool active)
    {
        byte pattern = QueryFamily.ComputeAnswerPattern(balance, active);
        byte classId = GetOrRegisterPattern(pattern);
        SetClassId(id, classId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetExplicitPattern(long id, byte pattern)
    {
        byte classId = GetOrRegisterPattern(pattern);
        SetClassId(id, classId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetClassId(long id, byte classId)
    {
        long byteIdx = id >> 1;
        bool isHigh = (id & 1) == 0;
        ref byte target = ref _data[byteIdx];

        byte current, updated;
        do
        {
            current = Volatile.Read(ref target);
            if (isHigh)
                updated = (byte)((current & 0x0F) | (classId << 4));
            else
                updated = (byte)((current & 0xF0) | (classId & 0x0F));
        } while (Interlocked.CompareExchange(ref target, updated, current) != current);
    }
}
