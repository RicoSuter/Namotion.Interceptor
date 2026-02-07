using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

[Trait("Category", "Integration")]
public class ProtocolHandshakeTests
{
    private readonly ITestOutputHelper _output;
    private readonly JsonWebSocketSerializer _serializer = new();

    public ProtocolHandshakeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Hello_WithWrongProtocolVersion_ShouldReceiveErrorAndClose()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);

        using var rawClient = new ClientWebSocket();
        await rawClient.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        // Send Hello with wrong version
        var hello = new HelloPayload { Version = 999, Format = WebSocketFormat.Json };
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, hello);
        await rawClient.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        // Should receive Error message
        var receiveBuffer = new byte[4096];
        var result = await rawClient.ReceiveAsync(receiveBuffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(
            new ReadOnlySpan<byte>(receiveBuffer, 0, result.Count));
        Assert.Equal(MessageType.Error, messageType);

        var error = _serializer.Deserialize<ErrorPayload>(
            new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));
        Assert.Equal(ErrorCode.VersionMismatch, error.Code);
        Assert.Contains("999", error.Message);

        // Server should close the connection (graceful Close or connection abort)
        try
        {
            var closeResult = await rawClient.ReceiveAsync(receiveBuffer, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Close, closeResult.MessageType);
        }
        catch (WebSocketException)
        {
            // Server may abort connection after sending Error + Close,
            // which manifests as a WebSocketException on the client side.
            // This is acceptable -- the key assertion is that we received the Error above.
        }
    }

    [Fact]
    public async Task Hello_WithCorrectVersion_ShouldReceiveWelcomeWithState()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "TestValue",
            port: portLease.Port);

        using var rawClient = new ClientWebSocket();
        await rawClient.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var hello = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, null, hello);
        await rawClient.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        var result = await rawClient.ReceiveAsync(receiveBuffer, CancellationToken.None);

        var (messageType, _, payloadStart, payloadLength) = _serializer.DeserializeMessageEnvelope(
            new ReadOnlySpan<byte>(receiveBuffer, 0, result.Count));
        Assert.Equal(MessageType.Welcome, messageType);

        var welcome = _serializer.Deserialize<WelcomePayload>(
            new ReadOnlySpan<byte>(receiveBuffer, payloadStart, payloadLength));
        Assert.Equal(1, welcome.Version);
        Assert.NotNull(welcome.State);

        // Gracefully close -- server may have already started shutting down
        // during test teardown, so tolerate WebSocketException here.
        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await rawClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closeCts.Token);
        }
        catch (WebSocketException)
        {
            // Acceptable -- server may abort connection during disposal.
        }
        catch (OperationCanceledException)
        {
            // Close timed out -- acceptable in test environment.
        }
    }

    [Fact]
    public async Task NoHello_WithinTimeout_ShouldCloseConnection()
    {
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            port: portLease.Port,
            configureServer: configuration => configuration.HelloTimeout = TimeSpan.FromMilliseconds(500));

        using var rawClient = new ClientWebSocket();
        await rawClient.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        // Don't send Hello -- wait for server timeout.
        // The server will close the connection after HelloTimeout expires.
        // Depending on timing, we may receive a graceful Close frame or a WebSocketException
        // (if the server aborts the socket after the close handshake times out).
        var receiveBuffer = new byte[4096];
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        bool connectionClosed;
        try
        {
            var result = await rawClient.ReceiveAsync(receiveBuffer, timeoutCts.Token);
            connectionClosed = result.MessageType == WebSocketMessageType.Close;
        }
        catch (WebSocketException)
        {
            // Server aborted the connection -- still means the timeout worked.
            connectionClosed = true;
        }

        Assert.True(connectionClosed, "Server should close the connection when no Hello is received within the timeout");
    }
}
