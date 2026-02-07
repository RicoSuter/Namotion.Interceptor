using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

[Trait("Category", "Integration")]
public class SequenceNumberTests
{
    private readonly ITestOutputHelper _output;
    private readonly JsonWebSocketSerializer _serializer = new();

    public SequenceNumberTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Welcome_ShouldContainSequenceNumber()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        // Connect raw WebSocket to inspect Welcome payload
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        // Send Hello
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        // Receive Welcome
        var receiveBuffer = new byte[64 * 1024];
        var result = await ws.ReceiveAsync(receiveBuffer, CancellationToken.None);
        var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, result.Count);
        var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        Assert.Equal(MessageType.Welcome, messageType);
        var welcome = _serializer.Deserialize<WelcomePayload>(
            new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));
        Assert.True(welcome.Sequence >= 0, "Welcome sequence should be non-negative");

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ServerSequence_ShouldIncrementAfterBroadcast()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port);

        Assert.Equal(0L, server.Server!.CurrentSequence);

        // Connect a client so broadcasts actually go through
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);
        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Trigger an update
        server.Root!.Name = "Test";

        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server.CurrentSequence > 0,
            message: "Sequence should increment after broadcasting to connected clients");

        _output.WriteLine($"Sequence after update: {server.Server.CurrentSequence}");

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Updates_ShouldHaveMonotonicallyIncreasingSequence()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port);

        // Connect raw WebSocket
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        // Handshake
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Trigger multiple updates, waiting for each broadcast to complete
        server.Root!.Name = "Update1";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.CurrentSequence > 0,
            timeout: TimeSpan.FromSeconds(5),
            message: "First update should be broadcast");
        var sequenceAfterFirst = server.Server!.CurrentSequence;
        server.Root!.Name = "Update2";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.CurrentSequence > sequenceAfterFirst,
            timeout: TimeSpan.FromSeconds(5),
            message: "Second update should be broadcast");
        var sequenceAfterSecond = server.Server!.CurrentSequence;
        server.Root!.Name = "Update3";

        // Read updates and verify monotonic sequences
        long lastSequence = 0;
        var updatesReceived = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (updatesReceived < 3 && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var readResult = await ws.ReceiveAsync(receiveBuffer, cts.Token);
            var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, readResult.Count);
            var (messageType, envelopeSequence, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

            if (messageType == MessageType.Update)
            {
                var sequence = envelopeSequence ?? 0;
                _output.WriteLine($"Update sequence: {sequence}");
                Assert.True(sequence > lastSequence,
                    $"Sequence should be monotonically increasing: {sequence} > {lastSequence}");
                lastSequence = sequence;
                updatesReceived++;
            }
        }

        Assert.True(updatesReceived >= 1, "Should have received at least one update with sequence");

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Heartbeat_ShouldArriveWithCorrectSequence()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        // Connect raw WebSocket
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        // Handshake
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Wait for heartbeat
        var heartbeatReceived = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!heartbeatReceived && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var readResult = await ws.ReceiveAsync(receiveBuffer, cts.Token);
            var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, readResult.Count);
            var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

            if (messageType == MessageType.Heartbeat)
            {
                var heartbeat = _serializer.Deserialize<HeartbeatPayload>(
                    new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));
                _output.WriteLine($"Heartbeat received with sequence: {heartbeat.Sequence}");
                Assert.True(heartbeat.Sequence >= 0, "Heartbeat sequence should be non-negative");
                heartbeatReceived = true;
            }
        }

        Assert.True(heartbeatReceived, "Should have received a heartbeat");

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Client_ShouldReceiveUpdatesWithCorrectSequence()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Normal update flow - should work fine
        server.Root!.Name = "NormalUpdate";

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "NormalUpdate",
            message: "Client should receive normal update");

        _output.WriteLine("Normal update flow verified");
    }

    [Fact]
    public async Task Heartbeat_SequenceStaysConsistentDuringQuietPeriod()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        // Connect raw WebSocket
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        // Handshake
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Collect two heartbeats with no updates in between
        var heartbeats = new System.Collections.Generic.List<long>();
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (heartbeats.Count < 2 && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var readResult = await ws.ReceiveAsync(receiveBuffer, cts.Token);
            var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, readResult.Count);
            var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

            if (messageType == MessageType.Heartbeat)
            {
                var heartbeat = _serializer.Deserialize<HeartbeatPayload>(
                    new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));
                heartbeats.Add(heartbeat.Sequence);
                _output.WriteLine($"Heartbeat {heartbeats.Count}: sequence={heartbeat.Sequence}");
            }
        }

        Assert.Equal(2, heartbeats.Count);
        // During quiet period, heartbeat sequence should stay the same (no updates happened)
        Assert.Equal(heartbeats[0], heartbeats[1]);

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Heartbeat_SequenceAdvancesAfterUpdates()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await ws.ReceiveAsync(receiveBuffer, CancellationToken.None); // Welcome

        // Get first heartbeat sequence
        long firstHeartbeatSequence = -1;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (firstHeartbeatSequence < 0 && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var readResult = await ws.ReceiveAsync(receiveBuffer, cts.Token);
            var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, readResult.Count);
            var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

            if (messageType == MessageType.Heartbeat)
            {
                var heartbeat = _serializer.Deserialize<HeartbeatPayload>(
                    new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));
                firstHeartbeatSequence = heartbeat.Sequence;
                _output.WriteLine($"First heartbeat: sequence={heartbeat.Sequence}");
            }
        }

        // Trigger some updates
        server.Root!.Name = "A";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.CurrentSequence > firstHeartbeatSequence,
            timeout: TimeSpan.FromSeconds(5),
            message: "First update should be broadcast");
        server.Root!.Name = "B";

        // Drain any update messages and wait for next heartbeat
        long secondHeartbeatSequence = -1;
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (secondHeartbeatSequence < 0 && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var readResult = await ws.ReceiveAsync(receiveBuffer, cts.Token);
            var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, readResult.Count);
            var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

            if (messageType == MessageType.Heartbeat)
            {
                var heartbeat = _serializer.Deserialize<HeartbeatPayload>(
                    new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));
                secondHeartbeatSequence = heartbeat.Sequence;
                _output.WriteLine($"Second heartbeat: sequence={heartbeat.Sequence}");
            }
        }

        Assert.True(secondHeartbeatSequence > firstHeartbeatSequence,
            $"Heartbeat sequence should advance after updates: {secondHeartbeatSequence} > {firstHeartbeatSequence}");

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MultipleClients_ShouldReceiveSameSequenceNumbers()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port);

        // Connect two raw WebSocket clients
        using var ws1 = new ClientWebSocket();
        using var ws2 = new ClientWebSocket();
        await ws1.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);
        await ws2.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        // Handshake both
        var sendBuffer1 = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer1, MessageType.Hello, null, new HelloPayload());
        await ws1.SendAsync(sendBuffer1.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var sendBuffer2 = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer2, MessageType.Hello, null, new HelloPayload());
        await ws2.SendAsync(sendBuffer2.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer1 = new byte[64 * 1024];
        var receiveBuffer2 = new byte[64 * 1024];
        await ws1.ReceiveAsync(receiveBuffer1, CancellationToken.None); // Welcome
        await ws2.ReceiveAsync(receiveBuffer2, CancellationToken.None); // Welcome

        // Trigger updates
        server.Root!.Name = "BroadcastTest";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.CurrentSequence > 0,
            timeout: TimeSpan.FromSeconds(5),
            message: "Update should be broadcast");

        // Both clients should receive the update with the same sequence number
        var seq1 = await ReceiveNextUpdateSequenceAsync(ws1, receiveBuffer1);
        var seq2 = await ReceiveNextUpdateSequenceAsync(ws2, receiveBuffer2);

        _output.WriteLine($"Client1 sequence: {seq1}, Client2 sequence: {seq2}");
        Assert.True(seq1 > 0, "Client1 should have received a sequenced update");
        Assert.Equal(seq1, seq2);

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Task.WhenAll(
                ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token),
                ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token));
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ClientReconnect_ShouldReceiveNewWelcomeSequence()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port);

        var receiveBuffer = new byte[64 * 1024];

        // First connection - get Welcome, then trigger updates while connected
        using var ws1 = new ClientWebSocket();
        await ws1.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, new HelloPayload());
        await ws1.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var result1 = await ws1.ReceiveAsync(receiveBuffer, CancellationToken.None);
        var bytes1 = new ReadOnlySpan<byte>(receiveBuffer, 0, result1.Count);
        var (msgType1, _, ps1, pl1) = _serializer.DeserializeMessageEnvelope(bytes1);
        Assert.Equal(MessageType.Welcome, msgType1);
        var welcome1 = _serializer.Deserialize<WelcomePayload>(new ReadOnlySpan<byte>(receiveBuffer, ps1, pl1));
        var welcome1Sequence = welcome1.Sequence;
        _output.WriteLine($"First Welcome sequence: {welcome1Sequence}");

        // Trigger updates while first client is still connected (so broadcast increments sequence)
        server.Root!.Name = "A";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.CurrentSequence > 0,
            timeout: TimeSpan.FromSeconds(5),
            message: "First update should be broadcast");
        var sequenceAfterA = server.Server!.CurrentSequence;
        server.Root!.Name = "B";
        await AsyncTestHelpers.WaitUntilAsync(
            () => server.Server!.CurrentSequence > sequenceAfterA,
            timeout: TimeSpan.FromSeconds(5),
            message: "Second update should be broadcast");

        // Close first client
        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }

        // Second connection should get a higher Welcome sequence
        var welcome2Sequence = await ConnectAndGetWelcomeSequenceAsync(portLease.Port, receiveBuffer);
        _output.WriteLine($"Second Welcome sequence: {welcome2Sequence}");

        Assert.True(welcome2Sequence > welcome1Sequence,
            $"Second Welcome should have higher sequence: {welcome2Sequence} > {welcome1Sequence}");
    }

    [Fact]
    public async Task Client_ShouldSurviveMultipleUpdatesWithoutFalseGapDetection()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Multiple updates - client should track sequences correctly
        for (var i = 1; i <= 10; i++)
        {
            var previousSequence = server.Server!.CurrentSequence;
            server.Root!.Name = $"Update{i}";
            await AsyncTestHelpers.WaitUntilAsync(
                () => server.Server!.CurrentSequence > previousSequence,
                timeout: TimeSpan.FromSeconds(5),
                message: $"Update {i} should be broadcast");
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Update10",
            message: "Client should receive all 10 updates without false gap detection");

        // Wait for at least one heartbeat cycle (1s interval) to verify no false gap detection
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        // Verify client is still connected and receiving updates
        server.Root!.Name = "AfterHeartbeat";
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterHeartbeat",
            message: "Client should still receive updates after heartbeat");

        _output.WriteLine("Client survived 10 rapid updates + heartbeat cycle without false gap detection");
    }

    [Fact]
    public async Task Client_ShouldResyncAfterServerRestart()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port,
            configureServer: config => config.HeartbeatInterval = TimeSpan.FromSeconds(1));

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Trigger updates before restart
        server.Root!.Name = "BeforeRestart";
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "BeforeRestart",
            message: "Client should receive update before restart");

        // Restart server - client should detect gap via heartbeat or connection loss
        await server.StopAsync();
        await server.RestartAsync();

        server.Root!.Name = "AfterRestart";

        // Client should reconnect and receive the new state
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "AfterRestart",
            timeout: TimeSpan.FromSeconds(30),
            message: "Client should resync after server restart");

        _output.WriteLine("Client successfully resynced after server restart");
    }

    private async Task<long> ReceiveNextUpdateSequenceAsync(ClientWebSocket webSocket, byte[] buffer)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var readResult = await webSocket.ReceiveAsync(buffer, cts.Token);
            var bytes = new ReadOnlySpan<byte>(buffer, 0, readResult.Count);
            var (messageType, envelopeSequence, _, _) = _serializer.DeserializeMessageEnvelope(bytes);

            if (messageType == MessageType.Update)
            {
                return envelopeSequence ?? -1;
            }
        }

        return -1;
    }

    private async Task<long> ConnectAndGetWelcomeSequenceAsync(int port, byte[] receiveBuffer)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/ws"), CancellationToken.None);

        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, new HelloPayload());
        await ws.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var result = await ws.ReceiveAsync(receiveBuffer, CancellationToken.None);
        var bytes = new ReadOnlySpan<byte>(receiveBuffer, 0, result.Count);
        var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(bytes);

        Assert.Equal(MessageType.Welcome, messageType);
        var welcome = _serializer.Deserialize<WelcomePayload>(
            new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));

        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }

        return welcome.Sequence;
    }
}
