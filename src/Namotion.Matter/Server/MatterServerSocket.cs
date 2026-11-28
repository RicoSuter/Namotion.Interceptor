using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Namotion.Matter.Server;

/// <summary>
/// The WebSocket to a matterjs-server over TCP (<c>ws</c>, <c>wss</c>) or a Unix socket (<c>unix</c>), which receives
/// whole messages into one pooled buffer that grows up to a maximum size. One receive and one send may run at a time.
/// </summary>
internal sealed class MatterServerSocket : IDisposable
{
    private const int InitialBufferSize = 16 * 1024;

    // A buffer grown beyond this is returned before the next receive, so one large message does not keep it rented.
    private const int RetainedBufferSize = 1024 * 1024;
    private static readonly Uri UnixSocketRequestUri = new("ws://localhost/ws");

    private readonly ClientWebSocket _socket;
    private readonly HttpMessageInvoker? _invoker;
    private readonly int _maxMessageSize;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);

    private MatterServerSocket(ClientWebSocket socket, HttpMessageInvoker? invoker, int maxMessageSize)
    {
        _socket = socket;
        _invoker = invoker;
        _maxMessageSize = maxMessageSize;
    }

    /// <exception cref="ArgumentException">The URL scheme is not supported.</exception>
    public static async Task<MatterServerSocket> ConnectAsync(Uri serverUrl, MatterServerOptions options, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = options.KeepAliveInterval;
        socket.Options.KeepAliveTimeout = options.KeepAliveTimeout;
        HttpMessageInvoker? invoker = null;
        try
        {
            switch (serverUrl.Scheme)
            {
                case "ws" or "wss":
                    await socket.ConnectAsync(serverUrl, cancellationToken).ConfigureAwait(false);
                    break;

                case "unix":
                    invoker = new HttpMessageInvoker(CreateUnixSocketHandler(serverUrl.LocalPath));
                    await socket.ConnectAsync(UnixSocketRequestUri, invoker, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentException($"The Matter server URL '{serverUrl}' must use the ws, wss or unix scheme.", nameof(serverUrl));
            }

            return new MatterServerSocket(socket, invoker, options.MaxMessageSize);
        }
        catch
        {
            socket.Dispose();
            invoker?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Receives the next message, valid until the next receive or <see cref="Dispose"/>; returns null when the server
    /// closed the connection. Canceling a receive aborts the connection.
    /// </summary>
    /// <exception cref="InvalidDataException">The message exceeds the maximum size.</exception>
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_buffer.Length > RetainedBufferSize)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        }

        var length = 0;
        while (true)
        {
            if (length == _buffer.Length)
            {
                GrowBuffer(length);
            }

            var result = await _socket.ReceiveAsync(_buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            length += result.Count;
            if (length > _maxMessageSize)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"A message from the Matter server exceeds the maximum size of {_maxMessageSize} bytes."));
            }

            if (result.EndOfMessage)
            {
                return _buffer.AsMemory(0, length);
            }
        }
    }

    /// <summary>Sends a text message. Canceling a send aborts the connection.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        _socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    /// <summary>
    /// Aborts and disposes the WebSocket, which ends a pending receive and send; safe to call from any thread. The
    /// buffer stays rented until <see cref="Dispose"/>.
    /// </summary>
    public void Abort() => _socket.Abort();

    /// <summary>Closes the socket and returns the buffer to the pool; call it only after the last receive ended.</summary>
    public void Dispose()
    {
        _socket.Dispose();
        _invoker?.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
    }

    /// <summary>Grows the full buffer; up to one byte past the maximum size, so an oversized message is detected.</summary>
    private void GrowBuffer(int length)
    {
        var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(2L * length, _maxMessageSize + 1L));
        _buffer.AsSpan(0, length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

    private static SocketsHttpHandler CreateUnixSocketHandler(string socketPath) => new()
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
