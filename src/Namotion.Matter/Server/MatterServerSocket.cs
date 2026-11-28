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
    public static async Task<MatterServerSocket> ConnectAsync(Uri serverUrl, int maxMessageSize, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
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

            return new MatterServerSocket(socket, invoker, maxMessageSize);
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
        var length = 0;
        while (true)
        {
            if (length == _buffer.Length)
            {
                GrowBuffer();
            }

            var result = await _socket.ReceiveAsync(_buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            length += result.Count;
            if (result.EndOfMessage)
            {
                return _buffer.AsMemory(0, length);
            }
        }
    }

    /// <summary>Sends a text message. Canceling a send aborts the connection.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        _socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    /// <summary>Closes the socket and returns the buffer to the pool; call it only after the last receive ended.</summary>
    public void Dispose()
    {
        _socket.Dispose();
        _invoker?.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
    }

    private void GrowBuffer()
    {
        if (_buffer.Length >= _maxMessageSize)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"A message from the Matter server exceeds the maximum size of {_maxMessageSize} bytes."));
        }

        var larger = ArrayPool<byte>.Shared.Rent(Math.Min(_buffer.Length * 2, _maxMessageSize));
        _buffer.CopyTo(larger, 0);
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
