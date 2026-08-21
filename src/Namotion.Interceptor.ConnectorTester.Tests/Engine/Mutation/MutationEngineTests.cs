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
        var engine = MutationEngine.CreateRandom(root, configuration, coordinator, NullLogger.Instance, disjointProperties: false);

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
        var engine = MutationEngine.CreateRandom(root, configuration, coordinator, NullLogger.Instance, disjointProperties: false);

        // Act
        engine.ResetCounters();

        // Assert
        Assert.Equal(0, engine.ValueMutationCount);
        Assert.Equal(0, engine.StructuralMutationCount);
    }

    [Fact]
    public async Task WhenModelRevertedAfterCommittedWrite_ThenVerifyWriteDurabilityReportsPrefixedViolation()
    {
        // A lost write looks exactly like this: the strategy commits, the ledger records what it
        // wrote, and a later revert (in production, a reconnect's complete-state load) leaves the
        // participant's own model disagreeing with its own ledger.

        // Arrange: participantIndex 0 with DisjointProperties on fixes every write to property 0
        // (StringValue) on this single-node graph, so the last recorded value is deterministic.
        var context = CreateContext();
        var root = new TestNode(context);
        var coordinator = new TestCycleCoordinator();
        var configuration = new ParticipantConfiguration
        {
            Name = "test",
            ValueMutationRate = 200,
            StructuralMutationRate = 0
        };
        var engine = MutationEngine.CreateRandom(root, configuration, coordinator, NullLogger.Instance, disjointProperties: true);

        await engine.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => engine.ValueMutationCount > 0,
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(20));
        await engine.StopAsync(CancellationToken.None);

        // Act: revert the model behind the ledger's back, as a reconnect would after a write
        // reached the participant's own model but never durably reached the server.
        root.StringValue = "reverted-by-lost-write";
        var violations = engine.VerifyWriteDurability();

        // Assert
        var violation = Assert.Single(violations);
        Assert.StartsWith("test:", violation);
        Assert.Contains("property 0", violation);
    }
}
