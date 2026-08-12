using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Server;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

/// <summary>
/// Pins that a server which has never listened says so, and that the standalone server reports its two
/// transport numbers through its own diagnostics rather than only through the handler it wraps. The
/// liveness transitions themselves need a bound port and are covered by
/// <see cref="WebSocketServerLivenessTests"/>.
/// </summary>
public class WebSocketServerDiagnosticsTests
{
    /// <summary>
    /// A compile-level pin of the member tree rather than behavioural coverage: every value asserted
    /// here is what a fresh <c>ConnectorMetrics</c> and an idle handler report, so this fails only if a
    /// member moves or changes type. The transitions are covered by
    /// <see cref="WebSocketServerLivenessTests"/>.
    /// </summary>
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
