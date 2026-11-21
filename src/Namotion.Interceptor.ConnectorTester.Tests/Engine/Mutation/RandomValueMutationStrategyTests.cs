using Namotion.Interceptor.Tracking.Transactions.Writers;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
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

public class RandomValueMutationStrategyTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
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

        using var cts = new CancellationTokenSource();

        // Act
        var run = strategy.RunAsync(cts.Token);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => counters.ValueMutationCount >= 1, message: "The unpaused strategy should mutate a value.");
        await cts.CancelAsync();
        try { await run; }
        catch (OperationCanceledException) { }
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
        await AsyncTestHelpers.WaitUntilAsync(() => counters.SourceWriteFailedCommitCount >= 1);
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
        await AsyncTestHelpers.WaitUntilAsync(() => counters.UnexpectedFailedCommitCount >= 1);
        await cts.CancelAsync();
        await run;

        // Assert
        Assert.Equal(0, counters.SourceWriteFailedCommitCount);
        Assert.Equal(0, counters.ConflictCommitCount);
    }

    [Fact]
    public async Task WhenNoLedgerIsGiven_ThenMoreThanOnePropertyIsMutated()
    {
        // Arrange
        var (root, strategy, counters) = CreateSingleNodeStrategy(participantIndex: 0, ledger: null);

        // Act
        await RunUntilValueMutationCountAsync(strategy, counters, 20);

        // Assert
        Assert.True(GetMutatedValueProperties(root).Length > 1);
    }

    [Theory]
    [InlineData(0, nameof(TestNode.StringValue))]
    [InlineData(1, nameof(TestNode.DecimalValue))]
    [InlineData(2, nameof(TestNode.IntValue))]
    [InlineData(3, nameof(TestNode.LongValue))]
    public async Task WhenALedgerIsGiven_ThenOnlyThePropertyAtTheParticipantIndexIsMutated(
        int participantIndex, string expectedProperty)
    {
        // Arrange
        var (root, strategy, counters) = CreateSingleNodeStrategy(participantIndex, new WriteDurabilityLedger());

        // Act
        await RunUntilValueMutationCountAsync(strategy, counters, 20);

        // Assert
        Assert.Equal([expectedProperty], GetMutatedValueProperties(root));
    }

    private static (TestNode Root, RandomValueMutationStrategy Strategy, MutationCounters Counters) CreateSingleNodeStrategy(
        int participantIndex, WriteDurabilityLedger? ledger)
    {
        var context = CreateContext();
        var root = new TestNode(context);
        var graph = new KnownNodeGraph();
        graph.Rebuild(root);
        var counters = new MutationCounters();

        var strategy = new RandomValueMutationStrategy(
            graph, new TestCycleCoordinator(), context, counters,
            new ParticipantConfiguration { Name = "test", Index = participantIndex, ValueMutationRate = 1000 },
            NullLogger.Instance,
            ledger);
        return (root, strategy, counters);
    }

    private static string[] GetMutatedValueProperties(TestNode node)
        => new (string Name, bool IsMutated)[]
            {
                (nameof(TestNode.StringValue), node.StringValue != string.Empty),
                (nameof(TestNode.DecimalValue), node.DecimalValue != 0),
                (nameof(TestNode.IntValue), node.IntValue != 0),
                (nameof(TestNode.LongValue), node.LongValue != 0)
            }
            .Where(property => property.IsMutated)
            .Select(property => property.Name)
            .ToArray();

    private static async Task RunUntilValueMutationCountAsync(
        RandomValueMutationStrategy strategy, MutationCounters counters, int count)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var runTask = strategy.RunAsync(cancellationTokenSource.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => counters.ValueMutationCount >= count);
        await cancellationTokenSource.CancelAsync();
        await runTask;
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
