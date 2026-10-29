using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.HybridEngine.Contracts;
using NeuroCache.ResearchLab.HybridEngine.Routing;
using NeuroCache.ResearchLab.ShadowPilot.Contracts;

namespace NeuroCache.ResearchLab.ShadowPilot.Core;

public sealed class HybridShadowExecutor
{
    private readonly IHybridQueryRouter _hybridRouter;
    private readonly ShadowCircuitBreakerConfig _config;

    public HybridShadowExecutor(IHybridQueryRouter hybridRouter, ShadowCircuitBreakerConfig? config = null)
    {
        _hybridRouter = hybridRouter;
        _config = config ?? new ShadowCircuitBreakerConfig();
    }

    public async Task<(ShadowComparisonOutcome Outcome, QueryResult? Result, double LatencyNs, string? Error)> ExecuteShadowAsync(
        QueryRequest request,
        CancellationToken callerCt = default)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_config.ShadowQueryTimeoutMs));

        try
        {
            var task = _hybridRouter.ExecuteAsync(request, cts.Token);
            var completed = await Task.WhenAny(task, Task.Delay(_config.ShadowQueryTimeoutMs, cts.Token));

            if (completed != task)
            {
                sw.Stop();
                return (ShadowComparisonOutcome.ShadowTimeout, null, sw.Elapsed.TotalNanoseconds, $"Execution exceeded timeout of {_config.ShadowQueryTimeoutMs} ms");
            }

            var queryResult = await task;
            sw.Stop();

            if (!queryResult.Success)
            {
                return (ShadowComparisonOutcome.ShadowError, queryResult, sw.Elapsed.TotalNanoseconds, queryResult.ErrorMessage ?? "Hybrid returned failure");
            }

            return (ShadowComparisonOutcome.ExactMatch, queryResult, sw.Elapsed.TotalNanoseconds, null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return (ShadowComparisonOutcome.ShadowTimeout, null, sw.Elapsed.TotalNanoseconds, $"Shadow execution timed out after {_config.ShadowQueryTimeoutMs} ms");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (ShadowComparisonOutcome.ShadowError, null, sw.Elapsed.TotalNanoseconds, $"Isolated shadow exception: {ex.GetType().Name} - {ex.Message}");
        }
    }
}
