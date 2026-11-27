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

public class MutationEngineTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();

    [Fact]
    public async Task WhenStructuralMutationRateIsZero_ThenOnlyValueMutationsRun()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context);
        var coordinator = new TestCycleCoordinator();
        var configuration = new ParticipantConfiguration
        {
            Name = "test",
            ValueMutationRate = 100,
            StructuralMutationRate = 0
        };
        var engine = MutationEngine.CreateRandom(root, configuration, coordinator, NullLogger.Instance);

        // Act
        await engine.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => engine.ValueMutationCount > 0,
            pollInterval: TimeSpan.FromMilliseconds(20));
        await engine.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(engine.ValueMutationCount > 0);
        Assert.Equal(0, engine.StructuralMutationCount);
    }

    [Fact]
    public void WhenResetCountersCalled_ThenBothCountersZero()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context);
        var coordinator = new TestCycleCoordinator();
        var configuration = new ParticipantConfiguration { Name = "test", ValueMutationRate = 50 };
        var engine = MutationEngine.CreateRandom(root, configuration, coordinator, NullLogger.Instance);

        // Act
        engine.ResetCounters();

        // Assert
        Assert.Equal(0, engine.ValueMutationCount);
        Assert.Equal(0, engine.StructuralMutationCount);
    }

    [Theory]
    [InlineData(0)] // random mutation
    [InlineData(10)] // batch mutation
    public async Task WhenVerifyWriteDurabilityIsOff_ThenNoLossIsReported(int numberOfBatches)
    {
        // Arrange
        var root = new TestNode(CreateContext());
        var engine = await RunTwoValueMutationTicksAsync(
            root, numberOfBatches, verifyWriteDurability: false, useTransactions: false);
        OverwriteValueProperties(root);

        // Act
        var violations = engine.VerifyWriteDurability().Violations;

        // Assert
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task WhenWritesSurvive_ThenVerifyWriteDurabilityReportsNothing(int numberOfBatches)
    {
        // Arrange
        var root = new TestNode(CreateContext());
        var engine = await RunTwoValueMutationTicksAsync(
            root, numberOfBatches, verifyWriteDurability: true, useTransactions: false);

        // Act
        var violations = engine.VerifyWriteDurability().Violations;

        // Assert
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(10, false)]
    [InlineData(10, true)]
    public async Task WhenAWriteIsLost_ThenVerifyWriteDurabilityNamesTheParticipantAndProperty(
        int numberOfBatches, bool useTransactions)
    {
        // Arrange
        var root = new TestNode(CreateContext().WithTransactions());
        var engine = await RunTwoValueMutationTicksAsync(
            root, numberOfBatches, verifyWriteDurability: true, useTransactions);
        OverwriteValueProperties(root);

        // Act
        var violations = engine.VerifyWriteDurability().Violations;

        // Assert: participant index 0 writes StringValue only.
        var violation = Assert.Single(violations);
        Assert.StartsWith("test: StringValue: wrote ", violation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task WhenATransactionFailsToCommit_ThenItsWritesAreNotRecorded(int numberOfBatches)
    {
        // Arrange
        var context = CreateContext().WithSourceTransactions();
        var root = new TestNode(context);
        FailingSource.AttachTo(root);
        var engine = CreateEngine(root, numberOfBatches, verifyWriteDurability: true, useTransactions: true);
        await engine.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => engine.SourceWriteFailedCommitCount >= 2);
        await engine.StopAsync(CancellationToken.None);
        OverwriteValueProperties(root);

        // Act
        var violations = engine.VerifyWriteDurability().Violations;

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public async Task WhenTheSourceDroppedWritesInTheCycle_ThenALostWriteIsInconclusive()
    {
        // Arrange
        var root = new TestNode(CreateContext());
        var source = new FailingSource { RootSubject = root };
        source.Metrics.OutboundRetries.AddDropped(7);
        var engine = CreateEngine(root, numberOfBatches: 0, verifyWriteDurability: true, useTransactions: false, source);
        engine.ResetDurabilityLedger();
        await RunTwoValueMutationTicksAsync(engine);
        source.Metrics.OutboundRetries.AddDropped(2);
        OverwriteValueProperties(root);

        // Act
        var result = engine.VerifyWriteDurability();

        // Assert: only the drops since the reset count.
        Assert.Equal(2, result.DroppedWriteCount);
        Assert.Single(result.Violations);
        Assert.True(result.IsInconclusive);
    }

    private static MutationEngine CreateEngine(
        TestNode root,
        int numberOfBatches,
        bool verifyWriteDurability,
        bool useTransactions,
        ISubjectSource? source = null)
    {
        var configuration = new ParticipantConfiguration
        {
            Name = "test",
            ValueMutationRate = 100,
            UseTransactions = useTransactions
        };
        var coordinator = new TestCycleCoordinator();
        return numberOfBatches > 0
            ? MutationEngine.CreateBatch(root, configuration, coordinator, NullLogger.Instance,
                numberOfBatches, configuration.Index, verifyWriteDurability, source)
            : MutationEngine.CreateRandom(
                root, configuration, coordinator, NullLogger.Instance, verifyWriteDurability, source);
    }

    private static async Task<MutationEngine> RunTwoValueMutationTicksAsync(
        TestNode root, int numberOfBatches, bool verifyWriteDurability, bool useTransactions)
    {
        var engine = CreateEngine(root, numberOfBatches, verifyWriteDurability, useTransactions);
        await RunTwoValueMutationTicksAsync(engine);
        return engine;
    }

    private static async Task RunTwoValueMutationTicksAsync(MutationEngine engine)
    {
        await engine.StartAsync(CancellationToken.None);
        // The first tick has committed once the second has mutated.
        await AsyncTestHelpers.WaitUntilAsync(() => engine.ValueMutationCount >= 2);
        await engine.StopAsync(CancellationToken.None);
    }

    private static void OverwriteValueProperties(TestNode node)
    {
        node.StringValue = "lost";
        node.DecimalValue = -1;
        node.IntValue = -1;
        node.LongValue = -1;
    }
}
