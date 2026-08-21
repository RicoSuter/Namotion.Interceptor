using Xunit;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
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

    [Fact]
    public async Task WhenRunForEveryParticipantIndexUpToThePropertyCount_ThenNoTwoParticipantsMutateTheSameProperty()
    {
        // This strategy documents itself as disjoint unconditionally, regardless of DisjointProperties
        // (see its own remarks), which is what the write-durability oracle relies on whenever it is
        // combined with the option. Pinned here by actually running the strategy, one participant at a
        // time, and observing which of TestNode's own properties changed, rather than by re-reading the
        // participantIndex % MutablePropertyCount formula the strategy uses internally: a test built from
        // that same expression would keep passing even if the selection logic changed underneath it.

        // Arrange - the expected participant count tracks ConnectorTesterConfiguration.MutablePropertyCount,
        // the shared constant every mutation strategy and the ledger are built from, rather than a number
        // assumed here.
        var propertyCount = ConnectorTesterConfiguration.MutablePropertyCount;
        var mutatedPropertyPerParticipant = new List<string>();

        for (var participantIndex = 0; participantIndex < propertyCount; participantIndex++)
        {
            var context = CreateContext();
            var root = new TestNode(context);
            var before = (root.StringValue, root.DecimalValue, root.IntValue, root.LongValue);

            var graph = new KnownNodeGraph();
            graph.Rebuild(root);
            var counters = new MutationCounters();
            var coordinator = new TestCycleCoordinator();
            var strategy = new BatchValueMutationStrategy(
                graph, coordinator, context, counters,
                new ParticipantConfiguration { Name = $"participant-{participantIndex}", ValueMutationRate = 100, UseTransactions = false },
                numberOfBatches: 10,
                participantIndex: participantIndex,
                new WriteDurabilityLedger());

            using var cts = new CancellationTokenSource();
            var runTask = strategy.RunAsync(cts.Token);
            try
            {
                // Act
                await AsyncTestHelpers.WaitUntilAsync(
                    () => counters.ValueMutationCount > 0,
                    timeout: TimeSpan.FromSeconds(5),
                    pollInterval: TimeSpan.FromMilliseconds(20));
            }
            finally
            {
                await cts.CancelAsync();
                try { await runTask; } catch (OperationCanceledException) { }
            }

            var mutated = new List<string>();
            if (!Equals(root.StringValue, before.StringValue)) mutated.Add(nameof(TestNode.StringValue));
            if (!Equals(root.DecimalValue, before.DecimalValue)) mutated.Add(nameof(TestNode.DecimalValue));
            if (!Equals(root.IntValue, before.IntValue)) mutated.Add(nameof(TestNode.IntValue));
            if (!Equals(root.LongValue, before.LongValue)) mutated.Add(nameof(TestNode.LongValue));

            Assert.True(
                mutated.Count == 1,
                $"Expected participant {participantIndex} to mutate exactly one property, mutated: {string.Join(", ", mutated)}.");
            mutatedPropertyPerParticipant.Add(mutated[0]);
        }

        // Assert - every participant wrote a property none of the others did.
        Assert.Equal(propertyCount, mutatedPropertyPerParticipant.Distinct().Count());
    }
}
