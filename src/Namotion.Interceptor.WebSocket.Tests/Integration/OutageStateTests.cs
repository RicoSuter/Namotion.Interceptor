using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Client;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Verifies that the client source reports an outage rather than staying Synchronized while
/// disconnected. Unlike OPC UA, WebSocket already buffers at loss detection (see
/// WebSocketSubjectClientSource.RunMonitorLoopAsync's StartBuffering call), so this test is a
/// regression guard, not a bug reproduction: it exists to stop a future connector change from
/// silently reintroducing the OPC UA defect this feature closes.
/// </summary>
[Trait("Category", "Integration")]
public class OutageStateTests
{
    private readonly ITestOutputHelper _output;

    public OutageStateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTheConnectionIsLost_ThenTheSourceReportsConnectingUntilItRecovers()
    {
        // Arrange - server fixture copied from WebSocketServerClientTests.
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        // The client source is constructed directly (not through WebSocketTestClient, which only
        // exposes IHostedService) so it can be held as its concrete type for IFaultInjectable and
        // ISubjectSource.State.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();
        var root = new TestRoot(context);

        await using var source = new WebSocketSubjectClientSource(
            root,
            new WebSocketClientConfiguration
            {
                ServerUri = new Uri($"ws://localhost:{portLease.Port}/ws"),
                ReconnectDelay = TimeSpan.FromMilliseconds(200),
                MaxReconnectDelay = TimeSpan.FromSeconds(2)
            },
            NullLogger<WebSocketSubjectClientSource>.Instance);

        try
        {
            await source.StartAsync(CancellationToken.None);

            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Synchronized,
                timeout: TimeSpan.FromSeconds(30),
                message: "Initial sync should complete");
            var firstSynchronizedAt = source.LastSynchronizedAt;

            // Act - Disconnect is the soft fault: it aborts the socket without stopping the
            // connector, matching a real network blip.
            await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Disconnect, CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Connecting,
                timeout: TimeSpan.FromSeconds(15),
                message: "Source should report Connecting during the outage");
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.State == SourceState.Synchronized,
                timeout: TimeSpan.FromSeconds(30),
                message: "Source should recover to Synchronized after reconnecting");
            Assert.NotNull(firstSynchronizedAt);
            Assert.True(source.LastSynchronizedAt > firstSynchronizedAt);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }
}
