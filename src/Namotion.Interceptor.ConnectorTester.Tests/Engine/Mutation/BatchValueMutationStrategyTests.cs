using Xunit;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
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

    private static IInterceptorSubjectContext CreateTransactionContext()
        => CreateContext().WithSourceTransactions();

    /// <summary>
    /// Fails every write, as a disconnected connector would under chaos. Mirrors
    /// SubjectTransactionBenchmark.BenchmarkSource's minimal ISubjectSource shape.
    /// </summary>
    private sealed class FailingSource : ISubjectSource
    {
        public required IInterceptorSubject RootSubject { get; init; }

        public int WriteBatchSize => 0;

        public ValueTask<WriteResult> WriteChangesAsync(
            ReadOnlyMemory<Namotion.Interceptor.Tracking.Change.SubjectPropertyChange> changes,
            CancellationToken cancellationToken)
            => new(WriteResult.Failure(changes, new InvalidOperationException("Source is not connected")));

        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
            => Task.FromResult<Action?>(null);

        public SourceState State => SourceState.Synchronizing;

        public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? LastSynchronizedAt => null;

        public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

        ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

        public event EventHandler<SourceEvent>? StateChanged
        {
            add { }
            remove { }
        }
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
            participantIndex: 0);

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
                participantIndex: 0);

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
    public async Task WhenCommitFailsWithTransactionException_ThenStrategyKeepsRunningAndCountsFailedCommit()
    {
        // Arrange: every property is owned by a source that fails every write, as a disconnected
        // chaos target would, so each batch's CommitAsync throws SubjectTransactionException.
        var context = CreateTransactionContext();
        var root = new TestNode(context);
        var source = new FailingSource { RootSubject = root };
        new PropertyReference(root, nameof(TestNode.StringValue)).SetSource(source);
        new PropertyReference(root, nameof(TestNode.DecimalValue)).SetSource(source);
        new PropertyReference(root, nameof(TestNode.IntValue)).SetSource(source);
        new PropertyReference(root, nameof(TestNode.LongValue)).SetSource(source);

        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var counters = new MutationCounters();
        var coordinator = new TestCycleCoordinator();
        var strategy = new BatchValueMutationStrategy(
            graph, coordinator, context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 100, UseTransactions = true },
            numberOfBatches: 1,
            participantIndex: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        // Act
        try { await strategy.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: the strategy absorbed every failed commit instead of letting the exception
        // escape RunAsync, and reports them through the dedicated counter.
        Assert.True(counters.FailedCommitCount >= 1);
        Assert.True(counters.ValueMutationCount >= 1);
    }
}
