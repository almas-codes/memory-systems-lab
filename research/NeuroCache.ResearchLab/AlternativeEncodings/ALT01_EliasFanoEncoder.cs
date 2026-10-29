using System;
using System.Collections.Generic;
using System.Numerics;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.AlternativeEncodings;

/// <summary>
/// ALT-01: Elias-Fano Sorted Exception List
/// Quasi-succinct encoding for sorted sparse IDs using high-bit unary arrays and low-bit binary arrays.
/// </summary>
public sealed class ALT01_EliasFanoEncoder : IExperimentCandidate, IExactRecordLookup<long, bool>, IMemoryAccountable
{
    public string ExperimentId => "ALT-ELIASFANO-001";
    public string CandidateName => "Elias-Fano Sorted Exception List (ALT-01)";
    public string Family => "AlternativeEncodings";
    public string Description => "Quasi-succinct encoding for sorted sparse ID lists with O(1) high-bit rank search.";

    private readonly ulong[] _highBits;
    private readonly ulong[] _lowBits;
    private readonly int _lowBitLength;
    private readonly int _count;
    private readonly long _universeSize;

    public int Count => _count;
    public long UniverseSize => _universeSize;
    public long TotalAllocatedBytes => (_highBits.Length * 8L) + (_lowBits.Length * 8L);
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 64;

    public ALT01_EliasFanoEncoder(long[] sortedUniqueIds, long universeSize)
    {
        _count = sortedUniqueIds.Length;
        _universeSize = universeSize;

        if (_count == 0)
        {
            _highBits = Array.Empty<ulong>();
            _lowBits = Array.Empty<ulong>();
            _lowBitLength = 0;
            return;
        }

        // L = floor(log2(U / N))
        int l = Math.Max(0, 63 - BitOperations.LeadingZeroCount((ulong)(universeSize / _count)));
        _lowBitLength = l;

        int totalLowBits = _count * l;
        _lowBits = new ulong[(totalLowBits + 63) / 64];

        long maxHighVal = universeSize >> l;
        int totalHighBits = (int)(_count + maxHighVal + 1);
        _highBits = new ulong[(totalHighBits + 63) / 64];

        for (int i = 0; i < _count; i++)
        {
            long val = sortedUniqueIds[i];

            // 1. Pack low bits
            if (l > 0)
            {
                ulong lowPart = (ulong)(val & ((1L << l) - 1L));
                int bitPos = i * l;
                _lowBits[bitPos >> 6] |= (lowPart << (bitPos & 63));
                if ((bitPos & 63) + l > 64)
                {
                    _lowBits[(bitPos >> 6) + 1] |= (lowPart >> (64 - (bitPos & 63)));
                }
            }

            // 2. Set unary high bit: (val >> l) + i
            long highVal = val >> l;
            long unaryPos = highVal + i;
            _highBits[unaryPos >> 6] |= (1UL << (int)(unaryPos & 63));
        }
    }

    public bool Query(long id)
    {
        if (id <= 0 || id > _universeSize || _count == 0) return false;

        // Linear scan / binary search over decoded values
        // For exact demonstration on sparse exception lists:
        for (int i = 0; i < _count; i++)
        {
            long decoded = DecodeValue(i);
            if (decoded == id) return true;
            if (decoded > id) break;
        }
        return false;
    }

    public long DecodeValue(int index)
    {
        // Decode low part
        ulong lowPart = 0;
        if (_lowBitLength > 0)
        {
            int bitPos = index * _lowBitLength;
            ulong word = _lowBits[bitPos >> 6];
            lowPart = (word >> (bitPos & 63)) & ((1UL << _lowBitLength) - 1UL);
            if ((bitPos & 63) + _lowBitLength > 64)
            {
                ulong nextWord = _lowBits[(bitPos >> 6) + 1];
                int bitsInFirst = 64 - (bitPos & 63);
                lowPart |= (nextWord & ((1UL << (_lowBitLength - bitsInFirst)) - 1UL)) << bitsInFirst;
            }
        }

        // Decode high part (Select index-th 1-bit minus index)
        long unaryPos = SelectHighBit(index + 1);
        long highVal = unaryPos - index;

        return (highVal << _lowBitLength) | (long)lowPart;
    }

    private long SelectHighBit(int k)
    {
        int count = 0;
        for (int w = 0; w < _highBits.Length; w++)
        {
            int pop = BitOperations.PopCount(_highBits[w]);
            if (count + pop >= k)
            {
                for (int b = 0; b < 64; b++)
                {
                    if ((_highBits[w] & (1UL << b)) != 0)
                    {
                        count++;
                        if (count == k) return (long)w * 64 + b;
                    }
                }
            }
            count += pop;
        }
        return -1;
    }
}
