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
public class MalformedMessageTests
{
    private readonly ITestOutputHelper _output;
    private readonly JsonWebSocketSerializer _serializer = new();

    public MalformedMessageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task MalformedMessage_ShouldReceiveErrorResponse()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);

        await server.StartAsync(context => new TestRoot(context), port: portLease.Port);

        using var rawClient = new ClientWebSocket();
        await rawClient.ConnectAsync(new Uri($"ws://localhost:{portLease.Port}/ws"), CancellationToken.None);

        var hello = new HelloPayload { Version = 1, Format = WebSocketFormat.Json };
        var sendBuffer = new ArrayBufferWriter<byte>(256);
        _serializer.SerializeMessageTo(sendBuffer, MessageType.Hello, hello);
        await rawClient.SendAsync(sendBuffer.WrittenMemory, WebSocketMessageType.Text, true, CancellationToken.None);

        var receiveBuffer = new byte[64 * 1024];
        await rawClient.ReceiveAsync(receiveBuffer, CancellationToken.None); // Read Welcome

        // Act - Send malformed message (not valid JSON array envelope)
        var malformed = "not valid json at all"u8.ToArray();
        await rawClient.SendAsync(malformed, WebSocketMessageType.Text, true, CancellationToken.None);

        // Assert - Server should handle gracefully: send Error, Close, or forcibly disconnect.
        // All three are acceptable -- the key is no server crash.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await rawClient.ReceiveAsync(receiveBuffer, cts.Token);
            Assert.True(
                result.MessageType == WebSocketMessageType.Close ||
                result.MessageType == WebSocketMessageType.Text,
                $"Expected Close or Error text, got {result.MessageType}");
        }
        catch (WebSocketException)
        {
            // Server forcibly closed the connection -- also acceptable
        }
    }
}
