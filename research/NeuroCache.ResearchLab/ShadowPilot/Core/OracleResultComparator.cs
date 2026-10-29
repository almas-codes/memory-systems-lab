using System;
using System.Collections.Generic;
using System.Linq;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.ShadowPilot.Contracts;

namespace NeuroCache.ResearchLab.ShadowPilot.Core;

public sealed class OracleResultComparator
{
    public ShadowComparisonRecord Compare(
        long requestId,
        RequestType type,
        object? authoritativeResult,
        object? shadowResult,
        double authoritativeLatencyNs,
        double shadowLatencyNs,
        long authoritativeVersion,
        long shadowVersion,
        string? customNotes = null)
    {
        if (shadowResult == null && authoritativeResult != null)
        {
            return new ShadowComparisonRecord(
                requestId,
                ShadowComparisonOutcome.ShadowError,
                authoritativeResult,
                null,
                authoritativeLatencyNs,
                shadowLatencyNs,
                authoritativeVersion,
                shadowVersion,
                customNotes ?? "Shadow result was null while authoritative returned value");
        }

        bool areEqual = CheckEquality(type, authoritativeResult, shadowResult);

        if (areEqual)
        {
            return new ShadowComparisonRecord(
                requestId,
                ShadowComparisonOutcome.ExactMatch,
                authoritativeResult,
                shadowResult,
                authoritativeLatencyNs,
                shadowLatencyNs,
                authoritativeVersion,
                shadowVersion,
                customNotes);
        }

        // Results differ. Check if version skew accounts for the difference.
        if (authoritativeVersion != shadowVersion)
        {
            string note = $"Version skew detected: Authoritative v{authoritativeVersion} vs Shadow v{shadowVersion}. Values: Auth={FormatResult(authoritativeResult)}, Shadow={FormatResult(shadowResult)}";
            return new ShadowComparisonRecord(
                requestId,
                ShadowComparisonOutcome.VersionSkewMismatch,
                authoritativeResult,
                shadowResult,
                authoritativeLatencyNs,
                shadowLatencyNs,
                authoritativeVersion,
                shadowVersion,
                note);
        }

        // Versions are identical, but results differed -> TRUE Predicate/Correctness mismatch!
        string errNote = $"PREDICATE MISMATCH at version {authoritativeVersion}: Auth={FormatResult(authoritativeResult)} != Shadow={FormatResult(shadowResult)}";
        return new ShadowComparisonRecord(
            requestId,
            ShadowComparisonOutcome.PredicateMismatch,
            authoritativeResult,
            shadowResult,
            authoritativeLatencyNs,
            shadowLatencyNs,
            authoritativeVersion,
            shadowVersion,
            errNote);
    }

    private static bool CheckEquality(RequestType type, object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;

        switch (type)
        {
            case RequestType.PointPredicate:
                if (a is bool bA && b is bool bB) return bA == bB;
                if (a is bool boolA && b is QueryResult qrB) return boolA == qrB.BooleanResult;
                if (a is QueryResult qrA && b is QueryResult qrB2) return qrA.BooleanResult == qrB2.BooleanResult;
                break;

            case RequestType.PointAll:
                if (a is byte byteA && b is byte byteB) return byteA == byteB;
                if (a is byte bA2 && b is QueryResult qrAll) return bA2 == qrAll.AnswerPattern;
                if (a is QueryResult qrAllA && b is QueryResult qrAllB) return qrAllA.AnswerPattern == qrAllB.AnswerPattern;
                break;

            case RequestType.AnalyticalCount:
                long countA = Convert.ToInt64(a is QueryResult qrCountA ? qrCountA.CountResult : a);
                long countB = Convert.ToInt64(b is QueryResult qrCountB ? qrCountB.CountResult : b);
                return countA == countB;

            case RequestType.AnalyticalBatch:
                long[]? arrA = a as long[] ?? (a as QueryResult)?.BatchResults;
                long[]? arrB = b as long[] ?? (b as QueryResult)?.BatchResults;
                if (arrA == null && arrB == null) return true;
                if (arrA == null || arrB == null) return false;
                if (arrA.Length != arrB.Length) return false;
                for (int i = 0; i < arrA.Length; i++)
                {
                    if (arrA[i] != arrB[i]) return false;
                }
                return true;

            case RequestType.AnalyticalIds:
                var listA = a as IReadOnlyList<long> ?? (a as QueryResult)?.MatchingIds;
                var listB = b as IReadOnlyList<long> ?? (b as QueryResult)?.MatchingIds;
                if (listA == null && listB == null) return true;
                if (listA == null || listB == null) return false;
                if (listA.Count != listB.Count) return false;
                for (int i = 0; i < listA.Count; i++)
                {
                    if (listA[i] != listB[i]) return false;
                }
                return true;
        }

        return Equals(a, b);
    }

    private static string FormatResult(object? res)
    {
        if (res == null) return "null";
        if (res is QueryResult qr)
        {
            if (qr.BatchResults != null) return $"Batch[{string.Join(",", qr.BatchResults)}]";
            if (qr.MatchingIds != null) return $"Ids(Count={qr.MatchingIds.Count})";
            if (qr.AnswerPattern != 0) return $"Pattern=0x{qr.AnswerPattern:X2}";
            return $"Bool={qr.BooleanResult}, Count={qr.CountResult}";
        }
        if (res is long[] arr) return $"Arr[{string.Join(",", arr)}]";
        return res.ToString() ?? "null";
    }
}
