using System;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Covers writes that reached the socket but were never confirmed applied before the connection was
/// lost. A successful SendAsync is not an acknowledgement, so these have to be re-parked into the
/// write retry queue on the next connect rather than silently lost.
/// </summary>
[Trait("Category", "Integration")]
public class InFlightWriteTests
{
    private readonly ITestOutputHelper _output;

    public InFlightWriteTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenAWriteWasSentButNeverApplied_ThenItIsReAssertedAfterReconnect()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), (_, root) => root.Name = "Initial", port: portLease.Port);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await AsyncTestHelpers.WaitUntilAsync(() => client.Root!.Name == "Initial");

        // Act: write, then kill the connection before the server can apply it, then let it come back.
        client.Root!.Name = "SentButNotApplied";
        await AsyncTestHelpers.WaitUntilAsync(() => client.Source!.InFlightCount > 0);
        await server.StopAsync();
        await server.RestartAsync();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Root!.Name == "SentButNotApplied",
            timeout: TimeSpan.FromSeconds(30),
            message: "The re-parked in-flight write should reach the restarted server");
    }

    // Fails against an in-flight set placed in WriteChangesViaRetryQueueAsync: a transactional commit
    // reaches WriteChangesAsync directly and never passes through that wrapper.
    [Fact]
    public async Task WhenTheWriteIsTransactional_ThenItAlsoEntersTheInFlightSet()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), (_, root) => root.Name = "Initial", port: portLease.Port);
        await client.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureContext: context => context.WithSourceTransactions());
        await AsyncTestHelpers.WaitUntilAsync(() => client.Root!.Name == "Initial");

        // Act
        using (var transaction = await client.Context!.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client.Root!.Name = "Committed";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.InFlightCount > 0,
            message: "A transactional write must be recorded in the in-flight set");
    }
}
