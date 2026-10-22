using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class BatchValueMutationStrategyTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();

    [Fact]
    public async Task WhenNodeCountIsZero_ThenStrategyReturnsImmediately()
    {
        // Arrange
        var context = CreateContext();
        var graph = new KnownNodeGraph(); // Rebuild not called: KnownNodes empty.
        var counters = new MutationCounters();
        var coordinator = new TestCycleCoordinator();
        var strategy = new BatchValueMutationStrategy(
            graph, coordinator, context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 1000, UseTransactions = false },
            numberOfBatches: 10,
            participantIndex: 0,
            NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        await strategy.RunAsync(cts.Token); // returns without throwing because nodeCount==0.

        // Assert
        Assert.Equal(0, counters.ValueMutationCount);
    }

    [Fact]
    public async Task WhenParallelBatchRuns_ThenAllMutationsObserveTheSameChangedTimestamp()
    {
        // Bug fix #1 regression: every mutation in a parallel batch must observe the same timestamp,
        // because the strategy re-enters the SubjectChangeContext scope inside each worker action.
        //
        // Arrange: install a custom GetTimestampFunction that records every call. If propagation is
        // broken, parallel workers fall through to GetTimestampFunction and record DateTimeOffset.UtcNow
        // (multiple distinct values). If propagation works, GetTimestampFunction is never called inside
        // the parallel block.
        var fallbackCallCount = 0;
        var originalGetter = SubjectChangeContext.GetTimestampFunction;
        SubjectChangeContext.GetTimestampFunction = () =>
        {
            Interlocked.Increment(ref fallbackCallCount);
            return DateTimeOffset.UtcNow;
        };

        try
        {
            var context = CreateContext();
            var nodes = new List<TestNode>();
            for (var i = 0; i < 100; i++)
            {
                nodes.Add(new TestNode(context));
            }
            var root = new TestNode(context) { Collection = nodes.ToArray() };
            var graph = new KnownNodeGraph();
            graph.Rebuild(root);
            var counters = new MutationCounters();
            var coordinator = new TestCycleCoordinator();
            var strategy = new BatchValueMutationStrategy(
                graph, coordinator, context, counters,
                new ParticipantConfiguration { Name = "test", ValueMutationRate = 100, UseTransactions = false },
                numberOfBatches: 1,    // one batch per second; batch size = 100.
                participantIndex: 0,
                NullLogger.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

            // Act
            try { await strategy.RunAsync(cts.Token); }
            catch (OperationCanceledException) { }

            // Assert: GetTimestampFunction must NOT be called from inside the parallel mutations,
            // because each worker re-enters the WithChangedTimestamp scope.
            // (It may be called once or twice from the strategy itself when capturing the batch timestamp.)
            Assert.True(fallbackCallCount < counters.ValueMutationCount,
                $"Expected GetTimestampFunction calls < mutations. Got {fallbackCallCount} fallback calls vs {counters.ValueMutationCount} mutations.");
        }
        finally
        {
            SubjectChangeContext.GetTimestampFunction = originalGetter;
        }
    }

    [Fact]
    public async Task WhenSourceWritesFail_ThenStrategyKeepsRunningAndCountsSourceWriteFailures()
    {
        // Arrange: every property is owned by a source that fails every write, as a disconnected
        // chaos target would, so each batch's CommitAsync throws SubjectTransactionException.
        var context = CreateContext().WithSourceTransactions();
        var root = new TestNode(context);
        FailingSource.AttachTo(root);

        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var counters = new MutationCounters();
        var coordinator = new TestCycleCoordinator();
        var strategy = new BatchValueMutationStrategy(
            graph, coordinator, context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 100, UseTransactions = true },
            numberOfBatches: 100,
            participantIndex: 0,
            NullLogger.Instance);
        using var cts = new CancellationTokenSource();

        // Act
        var run = strategy.RunAsync(cts.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => counters.SourceWriteFailedCommitCount >= 1, timeout: TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        try { await run; }
        catch (OperationCanceledException) { }

        // Assert
        Assert.Equal(0, counters.UnexpectedFailedCommitCount);
        Assert.True(counters.CommitCount >= counters.SourceWriteFailedCommitCount);
        Assert.True(counters.ValueMutationCount >= 1);
    }
}
