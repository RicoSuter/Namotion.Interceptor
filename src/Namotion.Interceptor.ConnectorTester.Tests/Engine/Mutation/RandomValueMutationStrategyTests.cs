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
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class RandomValueMutationStrategyTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

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
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 1000, UseTransactions = false },
            NullLogger.Instance);

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
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 1000, UseTransactions = false },
            NullLogger.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        try { await strategy.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: at least one mutation completed in 50ms at 1000/s.
        Assert.True(counters.ValueMutationCount >= 1);
    }

    [Fact]
    public async Task WhenSourceWritesFail_ThenStrategyKeepsRunningAndCountsSourceWriteFailures()
    {
        // Arrange: every property is owned by a source that fails every write, as a disconnected
        // chaos target would, so each transaction's CommitAsync throws SubjectTransactionException.
        var context = CreateContext().WithSourceTransactions();
        var root = new TestNode(context);
        FailingSource.AttachTo(root);
        var (counters, strategy) = CreateTransactionStrategy(context, root);
        using var cts = new CancellationTokenSource();

        // Act
        var run = strategy.RunAsync(cts.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => counters.SourceWriteFailedCommitCount >= 1, timeout: TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await run;

        // Assert
        Assert.Equal(0, counters.UnexpectedFailedCommitCount);
        Assert.True(counters.CommitCount >= counters.SourceWriteFailedCommitCount);
        Assert.True(counters.ValueMutationCount >= 1);
    }

    [Fact]
    public async Task WhenTransactionWriterThrows_ThenStrategyKeepsRunningAndCountsUnexpectedFailures()
    {
        // Arrange
        var context = CreateContext().WithTransactions();
        context.AddService<ITransactionWriter>(new ThrowingTransactionWriter());
        var root = new TestNode(context);
        var (counters, strategy) = CreateTransactionStrategy(context, root);
        using var cts = new CancellationTokenSource();

        // Act
        var run = strategy.RunAsync(cts.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => counters.UnexpectedFailedCommitCount >= 1, timeout: TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await run;

        // Assert
        Assert.Equal(0, counters.SourceWriteFailedCommitCount);
        Assert.Equal(0, counters.ConflictCommitCount);
    }

    private static (MutationCounters Counters, RandomValueMutationStrategy Strategy) CreateTransactionStrategy(
        IInterceptorSubjectContext context, TestNode root)
    {
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var counters = new MutationCounters();
        var strategy = new RandomValueMutationStrategy(
            graph, new TestCycleCoordinator(), context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 1000, UseTransactions = true },
            NullLogger.Instance);
        return (counters, strategy);
    }

    /// <summary>
    /// Throws from every source write, as a defective transaction writer would.
    /// </summary>
    private sealed class ThrowingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes, TransactionRequirement requirement, CancellationToken cancellationToken)
            => throw new NullReferenceException("Writer defect.");

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written, object? revertState, CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }
}
