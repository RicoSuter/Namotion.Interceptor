using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.WebSocket.Server;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

/// <summary>
/// Unit-level pin of the applied-through counter's stall rule on <see cref="WebSocketClientConnection"/>:
/// a failed apply must stop the count from advancing for the life of the connection, even once a later
/// update applies successfully. Exercises the connection directly, without a transport fault.
/// </summary>
public class WebSocketClientConnectionAppliedThroughTests
{
    [Fact]
    public void WhenAnApplyFails_ThenTheAppliedThroughCountStopsAdvancing()
    {
        // Arrange: a connection exercised directly through its update-received/applied/failed methods,
        // with no live socket and no apply behind them.
        var connection = new WebSocketClientConnection(new NoopWebSocket(), NullLogger.Instance);

        // Act: one update applies successfully, then one fails, then a later one applies successfully.
        var first = connection.OnUpdateReceived();
        connection.OnUpdateApplied(first);

        connection.OnUpdateReceived();
        connection.OnApplyFailed();

        var third = connection.OnUpdateReceived();
        connection.OnUpdateApplied(third);

        // Assert: the count stays at the last ordinal that applied before the failure. With the first
        // ordinal being 1, asserting only that the value is below it would also pass an implementation
        // that reset the count to zero on failure, which is a stall in name only: it would stop the
        // client from ever retiring anything further on this connection rather than holding it at the
        // point the failure actually reached. Pinning the exact ordinal rules that out, and also rules
        // out the count resuming past the failure to the later success.
        Assert.Equal(first, connection.AppliedThrough);
    }

    [Fact]
    public void WhenEveryApplySucceeds_ThenTheAppliedThroughCountAdvancesToTheLatestOrdinal()
    {
        // Arrange
        var connection = new WebSocketClientConnection(new NoopWebSocket(), NullLogger.Instance);

        // Act
        var first = connection.OnUpdateReceived();
        connection.OnUpdateApplied(first);
        var second = connection.OnUpdateReceived();
        connection.OnUpdateApplied(second);

        // Assert
        Assert.Equal(second, connection.AppliedThrough);
    }

    /// <summary>A WebSocket whose members are never called by these tests; only its shape is needed to construct a connection.</summary>
    private sealed class NoopWebSocket : System.Net.WebSockets.WebSocket
    {
        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
    }
}
