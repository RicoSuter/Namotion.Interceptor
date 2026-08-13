using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Client;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

/// <summary>
/// The client reports liveness from two places: the accepted handshake and the exit of the receive
/// loop that the handshake starts. Neither is observable without a real server.
/// </summary>
[Trait("Category", "Integration")]
public class WebSocketClientLivenessTests
{
    private readonly ITestOutputHelper _output;

    public WebSocketClientLivenessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheClientConnects_ThenItReportsOperationalUntilItStops()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port);

        // Act
        await source.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => source.Diagnostics.IsOperational,
            message: "The client should report operational once the handshake is accepted.");

        // Assert
        Assert.NotNull(source.Diagnostics.OperationalChangeTime);
        Assert.NotNull(source.Diagnostics.StartTime);

        // Act
        await source.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(source.Diagnostics.IsOperational);
    }

    [Fact]
    public async Task WhenTheConnectionDrops_ThenLivenessFallsAndRisesAgainOnReconnect()
    {
        // Arrange - a reconnect delay far longer than the poll interval below, so the client cannot
        // be back before the drop has been observed.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = await StartServerAsync(portLease.Port);
        await using var source = CreateClientSource(portLease.Port, reconnectDelay: TimeSpan.FromSeconds(3));

        await source.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "The client should report operational once the handshake is accepted.");

            var connectedAt = source.Diagnostics.OperationalChangeTime;

            // Act - Disconnect is the soft fault: it aborts the socket without stopping the connector,
            // so the monitor loop reconnects to the still-running server.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => !source.Diagnostics.IsOperational,
                message: "A client whose receive loop has exited should stop reporting that it is serving.");

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational,
                message: "The client should report operational again once it has reconnected.");

            // The rise is a second transition rather than the first one never having been dropped.
            Assert.True(source.Diagnostics.OperationalChangeTime > connectedAt);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    private async Task<WebSocketTestServer<TestRoot>> StartServerAsync(int port)
    {
        var server = new WebSocketTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: port);
        return server;
    }

    private static WebSocketSubjectClientSource CreateClientSource(int port, TimeSpan? reconnectDelay = null)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        return new WebSocketSubjectClientSource(
            new TestRoot(context),
            new WebSocketClientConfiguration
            {
                ServerUri = new Uri($"ws://localhost:{port}/ws"),
                ReconnectDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(200),
                MaxReconnectDelay = TimeSpan.FromSeconds(10)
            },
            NullLogger<WebSocketSubjectClientSource>.Instance);
    }
}
