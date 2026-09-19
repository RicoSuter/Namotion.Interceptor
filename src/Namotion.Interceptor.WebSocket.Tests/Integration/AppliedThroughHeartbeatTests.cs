using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.WebSocket.Internal;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Namotion.Interceptor.WebSocket.Server;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Covers the applied-through count the server reports on the heartbeat, which retires in-flight
/// entries the server has confirmed applying rather than carrying them until the next reconnect.
/// </summary>
[Trait("Category", "Integration")]
public class AppliedThroughHeartbeatTests
{
    private readonly ITestOutputHelper _output;

    public AppliedThroughHeartbeatTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheServerHasAppliedTheClientsUpdates_ThenTheHeartbeatRetiresTheInFlightEntries()
    {
        // Arrange: a connected client, with the server's heartbeat interval short enough to fire in-test.
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

        // Act
        client.Root!.Name = "Written";
        await AsyncTestHelpers.WaitUntilAsync(() => server.Root!.Name == "Written");

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.InFlightCount == 0,
            message: "The applied-through heartbeat should retire the in-flight entry");
    }

    [Fact]
    public async Task WhenTheServerAcknowledgedAWrite_ThenAReconnectDoesNotReAssertItOverANewerValue()
    {
        // Arrange: the client writes, the server applies it and reports it on a heartbeat, so the entry
        // is retired rather than carried.
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

        client.Root!.Name = "AcknowledgedByServer";
        await AsyncTestHelpers.WaitUntilAsync(() => server.Root!.Name == "AcknowledgedByServer");
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Source!.InFlightCount == 0,
            message: "The write should have been retired before the outage");

        // Act: the value moves on at the server while the client is away, then the client reconnects.
        await ((IFaultInjectable)client.Source!).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);
        server.Root!.Name = "NewerServerValue";

        // Assert: nothing is re-asserted, because there is no in-flight entry left to re-park. A single
        // sample the instant the value first matches cannot prove that: the reconnect's initial-state
        // load can land the newer value an instant before the reconcile that follows it takes the
        // restore branch and pushes the stale write back over it, and the first sample can land inside
        // that window. Require the match to hold for several consecutive polls instead of trusting the
        // first one, so a revert landing between samples is caught rather than missed.
        await WaitForStableMatchAsync(
            () => client.Root!.Name == "NewerServerValue" && server.Root!.Name == "NewerServerValue",
            message: "Client and server should stably converge on the value the server moved to while the client was away, not just momentarily match it");

        // If the write had been re-asserted, the resend would still be sitting here awaiting its own
        // acknowledgement.
        Assert.Equal(0, client.Source!.InFlightCount);
    }

    [Fact]
    public async Task WhenAnUpdateDropsPartOfItsContent_ThenTheAppliedThroughCountStallsAtIt()
    {
        // Arrange: a raw client, so an update can reference a subject ID the server can never resolve.
        // WebSocketSubjectClientSource never builds such a payload itself, since everything it sends is
        // locally resolvable, so reaching the server's drop path needs a hand-built message.
        var serializer = JsonWebSocketSerializer.Instance;
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: configuration => configuration.HeartbeatInterval = WebSocketServerConfiguration.MinimumHeartbeatInterval);

        using var rawClient = new ClientWebSocket();
        await rawClient.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var hello = new HelloPayload { Format = WebSocketFormat.Json };
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, hello);
        await rawClient.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        using (var welcomeResult = await WebSocketMessageReader.ReadMessageAsync(rawClient, 10 * 1024 * 1024, CancellationToken.None))
        {
            var (welcomeType, _, _) = serializer.DeserializeMessageEnvelope(welcomeResult.MessageBytes.Span);
            Assert.Equal(MessageType.Welcome, welcomeType);
        }

        // Act: an update whose only content references a subject ID the server has never seen and
        // cannot resolve, so the apply drops it without throwing, followed by a genuinely valid update
        // to the root's own property.
        var droppingUpdate = new SubjectUpdate
        {
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["ghost-subject"] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Ghost" }
                }
            }
        };
        await SendUpdateAsync(rawClient, serializer, droppingUpdate);

        var validUpdate = new SubjectUpdate
        {
            Root = "raw-root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["raw-root"] = new()
                {
                    [nameof(TestRoot.Name)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "AfterDrop" }
                }
            }
        };
        await SendUpdateAsync(rawClient, serializer, validUpdate);

        // Assert: the second update genuinely reached the model, proving the stall is not simply
        // because nothing else applied, and the heartbeat's applied-through count never advances past
        // the failed first update despite it.
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Root!.Name == "AfterDrop",
            message: "The valid update after the drop should still reach the model.");

        var heartbeat = await ReceiveHeartbeatAsync(rawClient, serializer, TimeSpan.FromSeconds(10));
        Assert.NotNull(heartbeat.AppliedThrough);
        Assert.Equal(0L, heartbeat.AppliedThrough!.Value);
    }

    private static async Task SendUpdateAsync(ClientWebSocket rawClient, JsonWebSocketSerializer serializer, SubjectUpdate update)
    {
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        serializer.SerializeMessageTo(sendBuffer, MessageType.Update, update);
        await rawClient.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>
    /// Reads messages until a Heartbeat arrives, skipping any Update or Error broadcasts in between,
    /// since either can legitimately interleave with the two sends above.
    /// </summary>
    private static async Task<HeartbeatPayload> ReceiveHeartbeatAsync(
        ClientWebSocket rawClient, JsonWebSocketSerializer serializer, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            using var result = await WebSocketMessageReader.ReadMessageAsync(rawClient, 10 * 1024 * 1024, cts.Token);
            var (messageType, payloadStart, payloadLength) = serializer.DeserializeMessageEnvelope(result.MessageBytes.Span);
            if (messageType == MessageType.Heartbeat)
            {
                return serializer.Deserialize<HeartbeatPayload>(result.MessageBytes.Span.Slice(payloadStart, payloadLength));
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds for several consecutive polls rather than trusting
    /// the first true sample, which a reconcile can revert an instant later. Reuses
    /// <see cref="AsyncTestHelpers.WaitUntilAsync"/>'s own poll interval as the spacing between samples,
    /// so consecutive successes are genuinely spread over time rather than evaluated back to back.
    /// </summary>
    private static Task WaitForStableMatchAsync(Func<bool> condition, string message)
    {
        const int requiredConsecutiveMatches = 5;

        var consecutiveMatches = 0;
        return AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                consecutiveMatches = condition() ? consecutiveMatches + 1 : 0;
                return consecutiveMatches >= requiredConsecutiveMatches;
            },
            message: message);
    }
}
