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
        // Arrange: a handler whose apply throws for one inbound update.
        var connection = new WebSocketClientConnection(new NoopWebSocket(), NullLogger.Instance);

        // Act
        var first = connection.OnUpdateReceived();
        connection.OnApplyFailed();
        var second = connection.OnUpdateReceived();
        connection.OnUpdateApplied(second);

        // Assert: a later success must not retire the update that failed.
        Assert.True(connection.AppliedThrough < first);
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
