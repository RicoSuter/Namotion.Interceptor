using System;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.WebSocket.Server;
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
        // Arrange - a short heartbeat interval, and a first write that is allowed to retire before the
        // write under test. Without that, the connection would end having claimed acknowledgement but
        // never actually observed an applied-through value on it, which section 2's fix now treats as
        // non-acknowledging: the in-flight write would be discarded rather than re-parked, which is not
        // what this test is about. One retirement is enough evidence for the rest of this connection's
        // life, including for the write under test that follows it.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: configuration => configuration.HeartbeatInterval = WebSocketServerConfiguration.MinimumHeartbeatInterval);
        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);
        await AsyncTestHelpers.WaitUntilAsync(() => client.Root!.Name == "Initial");

        client.Root!.Name = "Warmup";
        await AsyncTestHelpers.WaitUntilAsync(() => server.Root!.Name == "Warmup");
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.InFlightCount == 0,
            message: "The warmup write should retire, establishing that this connection's acknowledgement is genuine.");

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

        // The client must not have been left on the server's stale reloaded value either: the same
        // reconcile that re-sends the write also restores it locally, and that restore happens before
        // the resend, so it has already landed by the time the server's value above is observed.
        Assert.Equal("SentButNotApplied", client.Root!.Name);
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

        // Act - do not add another write here before the commit: the in-flight set is keyed per
        // property, so a second property write would push the count to two.
        using (var transaction = await client.Context!.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            client.Root!.Name = "Committed";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert - exactly one, not merely greater than zero: the property was never previously
        // published to a source, so this also pins that the regression this test guards against, the
        // set being recorded in the retry-queue wrapper instead of at the send site, would fail here
        // too, because a transactional commit never passes through that wrapper and the count would be
        // zero.
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.InFlightCount == 1,
            message: "A transactional write must be the one and only entry recorded in the in-flight set");
    }
}
