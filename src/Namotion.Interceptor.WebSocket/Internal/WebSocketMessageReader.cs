using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;

namespace Namotion.Interceptor.WebSocket.Internal;

/// <summary>
/// Utility for reading complete WebSocket messages with fragmentation handling,
/// buffer pooling, and size limit enforcement.
/// </summary>
internal static class WebSocketMessageReader
{
    private const int DefaultBufferSize = 64 * 1024;
    private static readonly RecyclableMemoryStreamManager MemoryStreamManager = new();

    /// <summary>
    /// Result of a WebSocket message read operation. Must be disposed after use to return pooled resources.
    /// </summary>
    public readonly struct ReadResult : IDisposable
    {
        private readonly RecyclableMemoryStream? _stream;
        private readonly byte[]? _rentedBuffer;
        private readonly int _length;

        /// <summary>
        /// True if the operation completed successfully with message data.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// True if the WebSocket received a close message.
        /// </summary>
        public bool IsCloseMessage { get; init; }

        /// <summary>
        /// True if the message exceeded the maximum allowed size.
        /// </summary>
        public bool ExceededMaxSize { get; init; }

        /// <summary>
        /// The complete message bytes. Only valid when Success is true.
        /// The memory is only valid until this ReadResult is disposed.
        /// </summary>
        public ReadOnlyMemory<byte> MessageBytes => _stream is not null
            ? new ReadOnlyMemory<byte>(_stream.GetBuffer(), 0, _length)
            : ReadOnlyMemory<byte>.Empty;

        private ReadResult(RecyclableMemoryStream? stream, byte[]? rentedBuffer, int length, bool success, bool isCloseMessage, bool exceededMaxSize)
        {
            _stream = stream;
            _rentedBuffer = rentedBuffer;
            _length = length;
            Success = success;
            IsCloseMessage = isCloseMessage;
            ExceededMaxSize = exceededMaxSize;
        }

        public static ReadResult Closed(byte[]? rentedBuffer) => new(null, rentedBuffer, 0, false, true, false);
        public static ReadResult MaxSizeExceeded(RecyclableMemoryStream? stream, byte[]? rentedBuffer) => new(stream, rentedBuffer, 0, false, false, true);
        public static ReadResult SuccessResult(RecyclableMemoryStream stream, byte[]? rentedBuffer, int length) => new(stream, rentedBuffer, length, true, false, false);

        // Factory methods for ReadMessageIntoStreamAsync where caller owns resources
        public static ReadResult ClosedNoResources => new(null, null, 0, false, true, false);
        public static ReadResult MaxSizeExceededNoResources => new(null, null, 0, false, false, true);
        public static ReadResult SuccessNoResources => new(null, null, 0, true, false, false);

        public void Dispose()
        {
            _stream?.Dispose();
            if (_rentedBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_rentedBuffer);
            }
        }
    }

    /// <summary>
    /// Reads a complete WebSocket message, handling fragmentation and enforcing size limits.
    /// The returned ReadResult must be disposed after use.
    /// </summary>
    /// <param name="webSocket">The WebSocket to read from.</param>
    /// <param name="maxMessageSize">Maximum allowed message size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The read result containing the message bytes or status information.</returns>
    public static async Task<ReadResult> ReadMessageAsync(
        System.Net.WebSockets.WebSocket webSocket,
        long maxMessageSize,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        var messageStream = MemoryStreamManager.GetStream();

        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    messageStream.Dispose();
                    return ReadResult.Closed(buffer);
                }

                if (messageStream.Length + result.Count > maxMessageSize)
                {
                    return ReadResult.MaxSizeExceeded(messageStream, buffer);
                }

                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            // Return the stream directly - caller will dispose to return to pool
            return ReadResult.SuccessResult(messageStream, buffer, (int)messageStream.Length);
        }
        catch
        {
            // On exception, clean up resources
            messageStream.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// Reads a complete WebSocket message with timeout support.
    /// </summary>
    /// <param name="webSocket">The WebSocket to read from.</param>
    /// <param name="maxMessageSize">Maximum allowed message size in bytes.</param>
    /// <param name="timeout">Timeout for the entire message (across all fragments).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The read result containing the message bytes or status information.</returns>
    public static async Task<ReadResult> ReadMessageWithTimeoutAsync(
        System.Net.WebSockets.WebSocket webSocket,
        long maxMessageSize,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        return await ReadMessageAsync(webSocket, maxMessageSize, timeoutCts.Token);
    }

    /// <summary>
    /// Reads a complete WebSocket message into the provided stream (for advanced scenarios with existing buffer management).
    /// </summary>
    /// <param name="webSocket">The WebSocket to read from.</param>
    /// <param name="buffer">Pre-rented buffer to use for receiving.</param>
    /// <param name="stream">Stream to write received bytes to.</param>
    /// <param name="maxMessageSize">Maximum allowed message size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The read result. If success, message bytes are in the provided stream.</returns>
    public static async Task<ReadResult> ReadMessageIntoStreamAsync(
        System.Net.WebSockets.WebSocket webSocket,
        byte[] buffer,
        System.IO.Stream stream,
        long maxMessageSize,
        CancellationToken cancellationToken)
    {
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return ReadResult.ClosedNoResources;
            }

            if (stream.Length + result.Count > maxMessageSize)
            {
                return ReadResult.MaxSizeExceededNoResources;
            }

            await stream.WriteAsync(buffer, 0, result.Count, cancellationToken);
        }
        while (!result.EndOfMessage);

        return ReadResult.SuccessNoResources; // Caller uses stream directly
    }
}
