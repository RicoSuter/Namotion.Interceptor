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

public class RandomValueMutationStrategyTests
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
            participantIndex: 0, disjointProperties: false, new WriteDurabilityLedger());

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
            participantIndex: 0, disjointProperties: false, new WriteDurabilityLedger());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        try { await strategy.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert: at least one mutation completed in 50ms at 1000/s.
        Assert.True(counters.ValueMutationCount >= 1);
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

        var strategy = new RandomValueMutationStrategy(
            graph, coordinator, context, counters,
            new ParticipantConfiguration { Name = "test", ValueMutationRate = 1000, UseTransactions = true },
            participantIndex: 0, disjointProperties: false, new WriteDurabilityLedger());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act: a failing commit must not escape the loop and kill the strategy.
        try { await strategy.RunAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // Assert
        Assert.True(counters.FailedCommitCount > 0);
    }
}
