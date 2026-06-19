using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Protocol;
using Namotion.Interceptor.WebSocket.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Client-side mirror of the server-side digest test
/// (<see cref="SequenceNumberTests.WhenClientHeartbeatDigestMismatchesServer_ThenServerRequestsResync"/>).
/// A controllable raw server sends a valid Welcome followed by a Heartbeat carrying a deliberately
/// wrong digest with an in-sync sequence. The client must detect the content-level divergence and
/// reconnect, which the existing reconnect/Welcome flow then heals.
/// </summary>
[Trait("Category", "Integration")]
public class ClientDigestRecoveryTests
{
    private readonly ITestOutputHelper _output;
    private readonly JsonWebSocketSerializer _serializer = new();

    public ClientDigestRecoveryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenServerHeartbeatDigestMismatchesClient_ThenClientReconnects()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();

        // Build a complete-state snapshot to send as the Welcome payload. Once the client applies it,
        // its registry holds a root with a value property, so its computed digest is non-empty
        // (the digest branch is skipped while the digest is empty).
        var snapshotContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var snapshotRoot = new TestRoot(snapshotContext) { Name = "Initial" };
        var state = SubjectUpdate.CreateCompleteUpdate(snapshotRoot, []);

        var acceptCount = 0;

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddConsole();
        });
        builder.WebHost.UseUrls($"http://localhost:{portLease.Port}");

        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var connectionNumber = Interlocked.Increment(ref acceptCount);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            var cancellationToken = context.RequestAborted;

            try
            {
                // Read the client Hello.
                await socket.ReceiveAsync(buffer, cancellationToken);

                // Send a valid Welcome so the client applies state.
                await SendAsync(socket, MessageType.Welcome,
                    new WelcomePayload { State = state, Sequence = 0 }, cancellationToken);

                if (connectionNumber > 1)
                {
                    // The reconnect that proves the test. Keep this connection healthy and idle.
                    await DrainUntilClosedAsync(socket, buffer, cancellationToken);
                    return;
                }

                // First connection: send a Heartbeat with an in-sync sequence (0 < expectedNext = 1)
                // but a deliberately wrong digest, isolating the digest branch from the gap branch.
                // The Welcome state is applied asynchronously after the client's connect returns, so the
                // first heartbeat may arrive before the client's digest is non-empty. Resend the bad
                // heartbeat in reply to every client ClientHeartbeat: this converges (without fixed
                // delays) as soon as the state is applied and the client's digest turns non-empty,
                // at which point the mismatch fires and the client reconnects (no further reply).
                await SendBadHeartbeatAsync(socket, cancellationToken);

                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    // The client only sends ClientHeartbeats on this connection; answer each with
                    // another mismatching heartbeat until the client detects the mismatch and leaves.
                    await SendBadHeartbeatAsync(socket, cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        });

        await app.StartAsync();

        await using var client = new WebSocketTestClient<TestRoot>(_output);
        try
        {
            await client.StartAsync(
                context => new TestRoot(context),
                port: portLease.Port,
                configureClient: configuration => configuration.ReconnectDelay = TimeSpan.FromMilliseconds(200));

            // Assert: the client detected the digest mismatch and reconnected (a second accept).
            await AsyncTestHelpers.WaitUntilAsync(
                () => Volatile.Read(ref acceptCount) >= 2,
                timeout: TimeSpan.FromSeconds(20),
                message: "Client should reconnect after a server heartbeat reports a mismatching state digest");

            _output.WriteLine("Client reconnected after digest mismatch as expected");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private Task SendBadHeartbeatAsync(System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken)
        => SendAsync(socket, MessageType.Heartbeat,
            new HeartbeatPayload { Sequence = 0, Digest = "DELIBERATELY-WRONG-DIGEST" }, cancellationToken);

    private Task SendAsync<TPayload>(
        System.Net.WebSockets.WebSocket socket,
        MessageType messageType,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var bytes = _serializer.SerializeMessage(messageType, payload);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task DrainUntilClosedAsync(
        System.Net.WebSockets.WebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
        }
    }
}
