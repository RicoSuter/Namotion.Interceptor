using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Namotion.Interceptor.WebSocket.Internal;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Internal;

public class WebSocketMessageReaderTests
{
    [Fact]
    public async Task ReadMessageAsync_SingleFragment_ShouldReturnMessageBytes()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var expectedText = "Hello, WebSocket!";
        webSocket.EnqueueTextMessage(expectedText);

        // Act
        using var result = await WebSocketMessageReader.ReadMessageAsync(
            webSocket, maxMessageSize: 1024, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.IsCloseMessage);
        Assert.False(result.ExceededMaxSize);
        Assert.Equal(expectedText, Encoding.UTF8.GetString(result.MessageBytes.Span));
    }

    [Fact]
    public async Task ReadMessageAsync_CloseMessage_ShouldReturnClosed()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        webSocket.EnqueueCloseMessage();

        // Act
        using var result = await WebSocketMessageReader.ReadMessageAsync(
            webSocket, maxMessageSize: 1024, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.True(result.IsCloseMessage);
        Assert.False(result.ExceededMaxSize);
        Assert.True(result.MessageBytes.IsEmpty);
    }

    [Fact]
    public async Task ReadMessageAsync_ExceedsMaxSize_ShouldReturnMaxSizeExceeded()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var largeData = new string('X', 200);
        webSocket.EnqueueTextMessage(largeData);

        // Act
        using var result = await WebSocketMessageReader.ReadMessageAsync(
            webSocket, maxMessageSize: 100, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.False(result.IsCloseMessage);
        Assert.True(result.ExceededMaxSize);
    }

    [Fact]
    public async Task ReadMessageAsync_Cancellation_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        webSocket.SetHanging();

        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var result = await WebSocketMessageReader.ReadMessageAsync(
                webSocket, maxMessageSize: 1024, cancellationTokenSource.Token);
        });
    }

    [Fact]
    public async Task ReadMessageWithTimeoutAsync_Timeout_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        webSocket.SetHanging();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var result = await WebSocketMessageReader.ReadMessageWithTimeoutAsync(
                webSocket, maxMessageSize: 1024, timeout: TimeSpan.FromMilliseconds(50), CancellationToken.None);
        });
    }

    [Fact]
    public async Task ReadMessageIntoStreamAsync_SingleFragment_ShouldWriteToStream()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var expectedText = "Stream test message";
        webSocket.EnqueueTextMessage(expectedText);

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var stream = new MemoryStream();

            // Act
            using var result = await WebSocketMessageReader.ReadMessageIntoStreamAsync(
                webSocket, buffer, stream, maxMessageSize: 1024, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            Assert.False(result.IsCloseMessage);
            Assert.False(result.ExceededMaxSize);

            var written = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            Assert.Equal(expectedText, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Fact]
    public async Task ReadMessageIntoStreamAsync_CloseMessage_ShouldReturnClosed()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        webSocket.EnqueueCloseMessage();

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var stream = new MemoryStream();

            // Act
            using var result = await WebSocketMessageReader.ReadMessageIntoStreamAsync(
                webSocket, buffer, stream, maxMessageSize: 1024, CancellationToken.None);

            // Assert
            Assert.False(result.Success);
            Assert.True(result.IsCloseMessage);
            Assert.False(result.ExceededMaxSize);
            Assert.Equal(0, stream.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Fact]
    public async Task ReadMessageIntoStreamAsync_ExceedsMaxSize_ShouldReturnMaxSizeExceeded()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var largeData = new string('Y', 300);
        webSocket.EnqueueTextMessage(largeData);

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var stream = new MemoryStream();

            // Act
            using var result = await WebSocketMessageReader.ReadMessageIntoStreamAsync(
                webSocket, buffer, stream, maxMessageSize: 100, CancellationToken.None);

            // Assert
            Assert.False(result.Success);
            Assert.False(result.IsCloseMessage);
            Assert.True(result.ExceededMaxSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Fact]
    public async Task ReadMessageIntoStreamAsync_Cancellation_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        webSocket.SetHanging();

        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var stream = new MemoryStream();

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var result = await WebSocketMessageReader.ReadMessageIntoStreamAsync(
                    webSocket, buffer, stream, maxMessageSize: 1024, cancellationTokenSource.Token);
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Fact]
    public async Task ReadMessageAsync_MultipleFragments_ShouldReassembleMessage()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var part1 = "Hello, ";
        var part2 = "WebSocket!";

        // Enqueue two fragments: first is not end-of-message, second is
        webSocket.EnqueueMessage(
            Encoding.UTF8.GetBytes(part1), WebSocketMessageType.Text, endOfMessage: false);
        webSocket.EnqueueMessage(
            Encoding.UTF8.GetBytes(part2), WebSocketMessageType.Text, endOfMessage: true);

        // Act
        using var result = await WebSocketMessageReader.ReadMessageAsync(
            webSocket, maxMessageSize: 1024, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.IsCloseMessage);
        Assert.False(result.ExceededMaxSize);
        Assert.Equal("Hello, WebSocket!", Encoding.UTF8.GetString(result.MessageBytes.Span));
    }

    [Fact]
    public async Task ReadMessageIntoStreamAsync_MultipleFragments_ShouldReassembleMessage()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var part1 = "Stream ";
        var part2 = "fragments!";

        webSocket.EnqueueMessage(
            Encoding.UTF8.GetBytes(part1), WebSocketMessageType.Text, endOfMessage: false);
        webSocket.EnqueueMessage(
            Encoding.UTF8.GetBytes(part2), WebSocketMessageType.Text, endOfMessage: true);

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var stream = new MemoryStream();

            // Act
            using var result = await WebSocketMessageReader.ReadMessageIntoStreamAsync(
                webSocket, buffer, stream, maxMessageSize: 1024, CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            var written = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            Assert.Equal("Stream fragments!", written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Fact]
    public async Task ReadMessageAsync_MultipleFragments_ExceedsMaxSizeOnSecondFragment_ShouldReturnMaxSizeExceeded()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var part1 = new string('A', 60);
        var part2 = new string('B', 60);

        // First fragment fits within the limit, but total exceeds maxMessageSize of 100
        webSocket.EnqueueMessage(
            Encoding.UTF8.GetBytes(part1), WebSocketMessageType.Text, endOfMessage: false);
        webSocket.EnqueueMessage(
            Encoding.UTF8.GetBytes(part2), WebSocketMessageType.Text, endOfMessage: true);

        // Act
        using var result = await WebSocketMessageReader.ReadMessageAsync(
            webSocket, maxMessageSize: 100, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.False(result.IsCloseMessage);
        Assert.True(result.ExceededMaxSize);
    }

    [Fact]
    public async Task ReadMessageAsync_EmptyMessage_ShouldReturnEmptySuccess()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        webSocket.EnqueueMessage([], WebSocketMessageType.Text, endOfMessage: true);

        // Act
        using var result = await WebSocketMessageReader.ReadMessageAsync(
            webSocket, maxMessageSize: 1024, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.IsCloseMessage);
        Assert.False(result.ExceededMaxSize);
        Assert.Equal(0, result.MessageBytes.Length);
    }

    [Fact]
    public async Task ReadMessageWithTimeoutAsync_CompletesBeforeTimeout_ShouldReturnSuccess()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var expectedText = "Quick message";
        webSocket.EnqueueTextMessage(expectedText);

        // Act
        using var result = await WebSocketMessageReader.ReadMessageWithTimeoutAsync(
            webSocket, maxMessageSize: 1024, timeout: TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(expectedText, Encoding.UTF8.GetString(result.MessageBytes.Span));
    }

    [Fact]
    public async Task ReadMessageAsync_BinaryMessage_ShouldReturnBinaryBytes()
    {
        // Arrange
        var webSocket = new FakeWebSocket();
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        webSocket.EnqueueMessage(binaryData, WebSocketMessageType.Binary, endOfMessage: true);

        // Act
        using var result = await WebSocketMessageReader.ReadMessageAsync(
            webSocket, maxMessageSize: 1024, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(binaryData, result.MessageBytes.ToArray());
    }

    /// <summary>
    /// Fake WebSocket for testing that allows configuring ReceiveAsync behavior.
    /// Supports enqueuing messages (including fragments), close frames, and hanging.
    /// </summary>
    private sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly Queue<(byte[] Data, WebSocketMessageType Type, bool EndOfMessage)> _messages = new();
        private WebSocketState _state = WebSocketState.Open;
        private TaskCompletionSource<WebSocketReceiveResult>? _hangTcs;

        public void EnqueueMessage(byte[] data, WebSocketMessageType type, bool endOfMessage)
        {
            _messages.Enqueue((data, type, endOfMessage));
        }

        public void EnqueueTextMessage(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _messages.Enqueue((bytes, WebSocketMessageType.Text, true));
        }

        public void EnqueueCloseMessage()
        {
            _messages.Enqueue((Array.Empty<byte>(), WebSocketMessageType.Close, true));
        }

        /// <summary>
        /// Makes ReceiveAsync hang forever until the cancellation token fires.
        /// </summary>
        public void SetHanging()
        {
            _hangTcs = new TaskCompletionSource<WebSocketReceiveResult>();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_hangTcs is not null)
            {
                using var registration = cancellationToken.Register(
                    () => _hangTcs.TrySetCanceled(cancellationToken));
                return await _hangTcs.Task.ConfigureAwait(false);
            }

            if (_messages.Count == 0)
            {
                throw new InvalidOperationException("No more messages queued in FakeWebSocket.");
            }

            var (data, type, endOfMessage) = _messages.Dequeue();

            if (type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var count = Math.Min(data.Length, buffer.Count);
            data.AsSpan(0, count).CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(count, type, endOfMessage);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose() { }
    }
}
