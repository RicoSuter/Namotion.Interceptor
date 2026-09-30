using Xunit;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

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
}
