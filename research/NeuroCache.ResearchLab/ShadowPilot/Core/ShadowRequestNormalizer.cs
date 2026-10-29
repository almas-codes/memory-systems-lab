using System;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.ShadowPilot.Contracts;

namespace NeuroCache.ResearchLab.ShadowPilot.Core;

public sealed class ShadowRequestNormalizer
{
    private readonly long _maxSupportedAccountId;

    public long MaxSupportedAccountId => _maxSupportedAccountId;

    public ShadowRequestNormalizer(long maxSupportedAccountId = 10_000_000)
    {
        _maxSupportedAccountId = maxSupportedAccountId > 0 ? maxSupportedAccountId : 10_000_000;
    }

    public (bool IsValid, bool IsSupported, string? ValidationError, QueryRequest? Query) Normalize(ShadowRequestRecord request)
    {
        if (request == null)
        {
            return (false, false, "Request cannot be null", null);
        }

        switch (request.Type)
        {
            case RequestType.PointPredicate:
                if (request.AccountId <= 0 || (request.MaxAccountId > 0 && request.AccountId > request.MaxAccountId))
                {
                    return (false, false, $"AccountId {request.AccountId} is outside valid range [1, {request.MaxAccountId}]", null);
                }
                if (request.PredicateIndex < 0 || request.PredicateIndex > 7)
                {
                    return (true, false, $"Predicate index {request.PredicateIndex} is outside declared family [0..7]", null);
                }

                var ptReq = new QueryRequest(
                    Type: RequestType.PointPredicate,
                    AccountId: request.AccountId,
                    PredicateIndex: request.PredicateIndex,
                    Consistency: request.Consistency,
                    RequiredVersion: request.RequiredVersion,
                    MaxAccountId: request.MaxAccountId > 0 ? request.MaxAccountId : _maxSupportedAccountId);
                return (true, true, null, ptReq);

            case RequestType.PointAll:
                if (request.AccountId <= 0 || (request.MaxAccountId > 0 && request.AccountId > request.MaxAccountId))
                {
                    return (false, false, $"AccountId {request.AccountId} is outside valid range [1, {request.MaxAccountId}]", null);
                }

                var allReq = new QueryRequest(
                    Type: RequestType.PointAll,
                    AccountId: request.AccountId,
                    Consistency: request.Consistency,
                    RequiredVersion: request.RequiredVersion,
                    MaxAccountId: request.MaxAccountId > 0 ? request.MaxAccountId : _maxSupportedAccountId);
                return (true, true, null, allReq);

            case RequestType.AnalyticalCount:
            case RequestType.AnalyticalIds:
            case RequestType.AnalyticalBatch:
                if (request.Analytical == null)
                {
                    return (false, false, "Analytical request must supply non-null Analytical parameters", null);
                }

                var anReq = new QueryRequest(
                    Type: request.Type,
                    Analytical: request.Analytical,
                    Consistency: request.Consistency,
                    RequiredVersion: request.RequiredVersion,
                    MaxAccountId: request.MaxAccountId > 0 ? request.MaxAccountId : _maxSupportedAccountId);
                return (true, true, null, anReq);

            case RequestType.CustomSql:
            default:
                return (true, false, $"Unsupported query type '{request.Type}'", null);
        }
    }
}
