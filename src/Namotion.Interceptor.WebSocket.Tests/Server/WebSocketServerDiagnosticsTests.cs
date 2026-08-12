using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

/// <summary>
/// Pins that a server which has never listened says so, and that the two transport numbers have a
/// single public spelling on the diagnostics rather than one on the server and one on the handler. The
/// liveness transitions themselves need a bound port and are covered by
/// <see cref="WebSocketServerLivenessTests"/>.
/// </summary>
public class WebSocketServerDiagnosticsTests
{
    [Fact]
    public void WhenNeverStarted_ThenTheServerReportsNotOperational()
    {
        // Arrange & Act
        using var server = CreateServer();

        // Assert
        Assert.False(server.Diagnostics.IsOperational);
        Assert.Null(server.Diagnostics.OperationalChangeTime);
        Assert.Null(server.Diagnostics.StartTime);
        Assert.Null(server.Diagnostics.LastError);
        Assert.Equal(0, server.Diagnostics.ConnectionCount);
        Assert.Equal(0L, server.Diagnostics.CurrentSequence);

        // Null rather than 0: the server measures neither direction.
        Assert.Null(server.Diagnostics.Throughput.IncomingPerSecond);
        Assert.Null(server.Diagnostics.Throughput.OutgoingPerSecond);
    }

    private static WebSocketSubjectServer CreateServer()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return new WebSocketSubjectServer(
            new TestRoot(context),
            new WebSocketServerConfiguration(),
            NullLogger<WebSocketSubjectServer>.Instance);
    }
}
