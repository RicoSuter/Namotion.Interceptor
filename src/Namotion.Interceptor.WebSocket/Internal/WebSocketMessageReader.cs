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
    /// Result of a WebSocket message read operation.
    /// </summary>
    public readonly struct ReadResult
    {
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
        /// </summary>
        public ReadOnlyMemory<byte> MessageBytes { get; init; }

        public static ReadResult Closed => new() { IsCloseMessage = true };
        public static ReadResult MaxSizeExceeded => new() { ExceededMaxSize = true };
        public static ReadResult SuccessResult(ReadOnlyMemory<byte> bytes) => new() { Success = true, MessageBytes = bytes };
    }

    /// <summary>
    /// Reads a complete WebSocket message, handling fragmentation and enforcing size limits.
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
        try
        {
            await using var messageStream = MemoryStreamManager.GetStream();

            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return ReadResult.Closed;
                }

                if (messageStream.Length + result.Count > maxMessageSize)
                {
                    return ReadResult.MaxSizeExceeded;
                }

                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            // Copy to array since the stream buffer will be returned to the pool
            var messageBytes = messageStream.ToArray();
            return ReadResult.SuccessResult(messageBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
                return ReadResult.Closed;
            }

            if (stream.Length + result.Count > maxMessageSize)
            {
                return ReadResult.MaxSizeExceeded;
            }

            await stream.WriteAsync(buffer, 0, result.Count, cancellationToken);
        }
        while (!result.EndOfMessage);

        return ReadResult.SuccessResult(ReadOnlyMemory<byte>.Empty); // Caller uses stream directly
    }
}
