using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeuroCache.ResearchLab.Contracts;
using NeuroCache.ResearchLab.HybridCompositions;
using NeuroCache.ResearchLab.Recovery;
using Xunit;

namespace NeuroCache.ResearchLab.ConcurrencyTests;

public class RepresentationConcurrencyTests
{
    private const int AccountCount = 5000;
    private const int DurationSeconds = 2;

    [Fact]
    public async Task REC01_CopyOnWrite_SupportsConcurrentReadersAndWriters()
    {
        var rep = new REC01_CopyOnWriteAnswerPages(AccountCount);
        await RunConcurrentStressTest(rep, rep);
    }

    [Fact]
    public async Task REC03_SeqlockPage_SupportsConcurrentReadersAndWriters()
    {
        var rep = new REC03_SeqlockPagePublication(AccountCount);
        await RunConcurrentStressTest(rep, rep);
    }

    [Fact]
    public async Task COMBO01_FactorizedAtomsWithDarg_SupportsConcurrentStress()
    {
        var rep = new COMBO01_FactorizedAtomsWithDargAndFreshness(AccountCount);
        await RunConcurrentStressTest(rep, rep);
    }

    [Fact]
    public async Task COMBO02_SemanticClasses_SupportsConcurrentStress()
    {
        var rep = new COMBO02_SemanticClassesWithRareExceptions(AccountCount);
        await RunConcurrentStressTest(rep, rep);
    }

    [Fact]
    public async Task COMBO06_CompletePortfolioRouter_SupportsConcurrentStress()
    {
        var rep = new COMBO06_CompletePortfolioRouter(AccountCount);
        await RunConcurrentStressTest(rep, rep);
    }

    private static async Task RunConcurrentStressTest(IPredicateEvaluator<long> reader, ISourceMutationHandler writer)
    {
        for (int i = 0; i < AccountCount; i++)
        {
            writer.HandleMutation(i, (i * 17) % 1200, (i % 2) == 0, 1);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(DurationSeconds));
        var token = cts.Token;

        long readOps = 0;
        long writeOps = 0;

        var readerTasks = Enumerable.Range(0, 4).Select(readerId => Task.Run(() =>
        {
            var rand = new Random(42 + readerId);
            while (!token.IsCancellationRequested)
            {
                long accId = rand.Next(0, AccountCount);
                int pred = rand.Next(0, 8);
                _ = reader.EvaluatePredicate(accId, pred);
                Interlocked.Increment(ref readOps);
            }
        })).ToArray();

        var writerTasks = Enumerable.Range(0, 2).Select(writerId => Task.Run(() =>
        {
            var rand = new Random(100 + writerId);
            long seq = 100;
            while (!token.IsCancellationRequested)
            {
                long accId = rand.Next(0, AccountCount);
                int bal = rand.Next(50, 1500);
                bool act = rand.Next(2) == 1;
                writer.HandleMutation(accId, bal, act, Interlocked.Increment(ref seq));
                Interlocked.Increment(ref writeOps);
            }
        })).ToArray();

        await Task.WhenAll(readerTasks.Concat(writerTasks));

        Assert.True(readOps > 500, $"Expected >500 reads, got {readOps}");
        Assert.True(writeOps > 50, $"Expected >50 writes, got {writeOps}");
    }
}
