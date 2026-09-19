using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
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
            timeout: TimeSpan.FromSeconds(5),
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

    [Fact]
    public async Task WhenPropertiesAreNotDisjoint_ThenWriteDurabilityIsNotVerified()
    {
        // Arrange
        var root = new TestNode(CreateContext());
        var engine = await RunTwoValueMutationTicksAsync(root, disjointProperties: false, useTransactions: false);
        OverwriteValueProperties(root);

        // Act
        var violations = engine.VerifyWriteDurability();

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public async Task WhenDisjointWritesSurvive_ThenVerifyWriteDurabilityReportsNothing()
    {
        // Arrange
        var root = new TestNode(CreateContext());
        var engine = await RunTwoValueMutationTicksAsync(root, disjointProperties: true, useTransactions: false);

        // Act
        var violations = engine.VerifyWriteDurability();

        // Assert
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenADisjointWriteIsLost_ThenVerifyWriteDurabilityNamesTheParticipantAndProperty(bool useTransactions)
    {
        // Arrange
        var root = new TestNode(CreateContext().WithTransactions());
        var engine = await RunTwoValueMutationTicksAsync(root, disjointProperties: true, useTransactions);
        OverwriteValueProperties(root);

        // Act
        var violations = engine.VerifyWriteDurability();

        // Assert: participant index 0 writes StringValue only.
        var violation = Assert.Single(violations);
        Assert.StartsWith("test: StringValue: wrote ", violation);
    }

    private static async Task<MutationEngine> RunTwoValueMutationTicksAsync(
        TestNode root, bool disjointProperties, bool useTransactions)
    {
        var configuration = new ParticipantConfiguration
        {
            Name = "test",
            ValueMutationRate = 100,
            UseTransactions = useTransactions
        };
        var engine = MutationEngine.CreateRandom(
            root, configuration, new TestCycleCoordinator(), NullLogger.Instance, disjointProperties);

        await engine.StartAsync(CancellationToken.None);
        // The first tick has committed once the second has mutated.
        await AsyncTestHelpers.WaitUntilAsync(() => engine.ValueMutationCount >= 2);
        await engine.StopAsync(CancellationToken.None);
        return engine;
    }

    private static void OverwriteValueProperties(TestNode node)
    {
        node.StringValue = "lost";
        node.DecimalValue = -1;
        node.IntValue = -1;
        node.LongValue = -1;
    }
}
