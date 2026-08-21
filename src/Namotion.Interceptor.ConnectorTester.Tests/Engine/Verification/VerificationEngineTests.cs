using System.Reflection;
using Xunit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Chaos;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class VerificationEngineTests
{
    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>
    /// Invokes the private CollectDurabilityViolations via reflection instead of widening its
    /// visibility: nothing outside VerificationEngine needs to call it, so the method stays
    /// private and the test reaches in directly to exercise the real gate.
    /// </summary>
    private static List<string> CollectDurabilityViolations(VerificationEngine verificationEngine)
    {
        var method = typeof(VerificationEngine)
            .GetMethod("CollectDurabilityViolations", BindingFlags.NonPublic | BindingFlags.Instance);
        return (List<string>)method!.Invoke(verificationEngine, null)!;
    }

    /// <summary>
    /// Produces a real ledger divergence: participantIndex 0 with DisjointProperties on fixes every
    /// write to property 0 (StringValue) on this single-node graph, so a random mutation engine is
    /// started, runs until it has committed at least one value mutation, is stopped, and then the
    /// model is reverted behind the ledger's back, as a reconnect's complete-state load would after a
    /// write reached the participant's own model but never durably reached the server.
    /// </summary>
    private static async Task<(TestNode Root, TestCycleCoordinator Coordinator, MutationEngine Engine)> CreateEngineWithARealLedgerDivergenceAsync()
    {
        var context = EngineTestContextFactory.Create();
        var root = new TestNode(context);
        var coordinator = new TestCycleCoordinator();
        var participantConfiguration = new ParticipantConfiguration
        {
            Name = "test",
            ValueMutationRate = 200,
            StructuralMutationRate = 0
        };
        var engine = MutationEngine.CreateRandom(root, participantConfiguration, coordinator, NullLogger.Instance, disjointProperties: true);

        await engine.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => engine.ValueMutationCount > 0,
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(20));
        await engine.StopAsync(CancellationToken.None);

        root.StringValue = "reverted-by-lost-write";

        return (root, coordinator, engine);
    }

    [Fact]
    public async Task WhenDisjointPropertiesOff_ThenCollectDurabilityViolationsIgnoresARealLedgerDivergence()
    {
        // The oracle's soundness rests entirely on this gate: with overlapping writers
        // (DisjointProperties off) a real recorded divergence must never surface as a
        // violation, because it could be a legitimate last-writer-wins overwrite instead of
        // a loss. This proves the gate holds even when the ledger genuinely has one.

        // Arrange
        var (root, coordinator, engine) = await CreateEngineWithARealLedgerDivergenceAsync();

        // Confirm the divergence is real before checking that the gate hides it.
        Assert.NotEmpty(engine.VerifyWriteDurability());

        var configuration = new ConnectorTesterConfiguration { DisjointProperties = false };
        var verificationEngine = new VerificationEngine(
            configuration,
            coordinator,
            new Dictionary<string, TestNode> { ["test"] = root },
            [engine],
            [],
            cycleRecorder: null,
            new FakeApplicationLifetime(),
            NullLogger.Instance,
            runDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Act
        var violations = CollectDurabilityViolations(verificationEngine);

        // Assert
        Assert.Empty(violations);
    }

    [Fact]
    public async Task WhenDisjointPropertiesOn_ThenCollectDurabilityViolationsSurfacesTheSameDivergence()
    {
        // Converse of the test above: with the gate open, the same real divergence is
        // reported, prefixed by the participant name.

        // Arrange
        var (root, coordinator, engine) = await CreateEngineWithARealLedgerDivergenceAsync();

        var configuration = new ConnectorTesterConfiguration { DisjointProperties = true };
        var verificationEngine = new VerificationEngine(
            configuration,
            coordinator,
            new Dictionary<string, TestNode> { ["test"] = root },
            [engine],
            [],
            cycleRecorder: null,
            new FakeApplicationLifetime(),
            NullLogger.Instance,
            runDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Act
        var violations = CollectDurabilityViolations(verificationEngine);

        // Assert
        var violation = Assert.Single(violations);
        Assert.StartsWith("test:", violation);
    }
}
