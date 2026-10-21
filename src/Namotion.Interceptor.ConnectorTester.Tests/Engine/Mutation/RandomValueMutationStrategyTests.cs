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

public class RandomValueMutationStrategyTests
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
    public async Task WhenCoordinatorIsPaused_ThenStrategyDoesNotMutate()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context);
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var counters = new MutationCounters();
        var coordinator = new TestCycleCoordinator();
        coordinator.Pause();

        var strategy = new RandomValueMutationStrategy(
            graph, coordinator, context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 1000, UseTransactions = false });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        try { await strategy.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: paused throughout, no mutations.
        Assert.Equal(0, counters.ValueMutationCount);
    }

    [Fact]
    public async Task WhenResumed_ThenStrategyIncrementsCounter()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context);
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var counters = new MutationCounters();
        var coordinator = new TestCycleCoordinator();
        // coordinator starts unpaused.

        var strategy = new RandomValueMutationStrategy(
            graph, coordinator, context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 1000, UseTransactions = false });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        try { await strategy.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: at least one mutation completed in 50ms at 1000/s.
        Assert.True(counters.ValueMutationCount >= 1);
    }

    [Fact]
    public async Task WhenCommitFailsWithTransactionException_ThenStrategyKeepsRunningAndCountsFailedCommit()
    {
        // Arrange: every property is owned by a source that fails every write, as a disconnected
        // chaos target would, so each transaction's CommitAsync throws SubjectTransactionException.
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

        var strategy = new RandomValueMutationStrategy(
            graph, coordinator, context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 1000, UseTransactions = true });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        try { await strategy.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: the strategy absorbed every failed commit instead of letting the exception
        // escape RunAsync, and reports them through the dedicated counter.
        Assert.True(counters.FailedCommitCount >= 1);
        Assert.True(counters.ValueMutationCount >= 1);
    }
}
