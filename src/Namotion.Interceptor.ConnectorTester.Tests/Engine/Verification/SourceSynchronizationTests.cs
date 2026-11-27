using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class SourceSynchronizationTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task WhenASourceSynchronizesLater_ThenWaitReturnsOnceItHas()
    {
        // Arrange
        var server = CreateEngine("server", verifyWriteDurability: true, source: null);
        var (client, source) = CreateClientEngine("client-a", verifyWriteDurability: true);
        var waitTask = SourceSynchronization.WaitAsync(
            [server, client], TestTimeouts.Default, PollInterval, CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        // Act
        source.State = SourceState.Synchronized;
        var unsynchronized = await waitTask.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(unsynchronized);
    }

    [Fact]
    public async Task WhenASourceWasSynchronizedOnceAndThenReconnects_ThenWaitStillCountsIt()
    {
        // Arrange
        var (clientA, sourceA) = CreateClientEngine("client-a", verifyWriteDurability: true);
        var (clientB, sourceB) = CreateClientEngine("client-b", verifyWriteDurability: true);
        sourceA.State = SourceState.Synchronized;
        var waitTask = SourceSynchronization.WaitAsync(
            [clientA, clientB], TestTimeouts.Default, PollInterval, CancellationToken.None);
        sourceA.State = SourceState.Synchronizing;

        // Act
        sourceB.State = SourceState.Synchronized;
        var unsynchronized = await waitTask.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(unsynchronized);
    }

    [Fact]
    public async Task WhenASourceDoesNotSynchronizeInTime_ThenWaitReturnsItsParticipant()
    {
        // Arrange
        var server = CreateEngine("server", verifyWriteDurability: true, source: null);
        var (client, _) = CreateClientEngine("client-a", verifyWriteDurability: true);

        // Act
        var unsynchronized = await SourceSynchronization.WaitAsync(
            [server, client], TimeSpan.Zero, PollInterval, CancellationToken.None);

        // Assert
        Assert.Equal(["client-a"], unsynchronized);
    }

    [Fact]
    public async Task WhenTheEngineDoesNotVerifyWriteDurability_ThenItCountsAsSynchronized()
    {
        // Arrange
        var (client, _) = CreateClientEngine("client-a", verifyWriteDurability: false);

        // Act
        var unsynchronized = await SourceSynchronization.WaitAsync(
            [client], TimeSpan.Zero, PollInterval, CancellationToken.None);

        // Assert
        Assert.Empty(unsynchronized);
    }

    private static (MutationEngine Engine, FailingSource Source) CreateClientEngine(string name, bool verifyWriteDurability)
    {
        var source = new FailingSource { RootSubject = new TestNode(InterceptorSubjectContext.Create()) };
        return (CreateEngine(name, verifyWriteDurability, source), source);
    }

    private static MutationEngine CreateEngine(string name, bool verifyWriteDurability, FailingSource? source)
    {
        var root = source is null
            ? new TestNode(InterceptorSubjectContext.Create())
            : (TestNode)source.RootSubject;
        var configuration = new ParticipantConfiguration { Name = name, ValueMutationRate = 1 };
        return MutationEngine.CreateRandom(
            root, configuration, new TestCycleCoordinator(), NullLogger.Instance, verifyWriteDurability, source);
    }
}
