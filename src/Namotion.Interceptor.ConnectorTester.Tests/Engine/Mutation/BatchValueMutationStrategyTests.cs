using Xunit;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

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

    /// <summary>Reports every commit as failed, without applying anything.</summary>
    private sealed class FailingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes, TransactionRequirement requirement, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Transport is down.");

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written, object? revertState, CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

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
            new WriteDurabilityLedger());

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
                new WriteDurabilityLedger());

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
    public async Task WhenCommitFailsUnderTransactions_ThenStrategyKeepsRunningAndCountsTheFailure()
    {
        // Arrange: every commit fails, as a dying transport would under BestEffort.
        var context = CreateContext().WithTransactions();
        context.AddService<ITransactionWriter>(new FailingTransactionWriter());
        var root = new TestNode(context);
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var counters = new MutationCounters();
        var coordinator = new TestCycleCoordinator();

        var strategy = new BatchValueMutationStrategy(
            graph, coordinator, context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 100, UseTransactions = true },
            numberOfBatches: 10,
            participantIndex: 0,
            new WriteDurabilityLedger());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Act: a failing commit must not escape the loop and kill the strategy.
        try { await strategy.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert
        Assert.True(counters.FailedCommitCount > 0);
    }
}
