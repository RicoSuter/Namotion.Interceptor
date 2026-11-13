using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Forwards TCP connections to an OPC UA server and can make the server unreachable while it keeps its
/// sessions and subscriptions, which a server restart cannot.
/// </summary>
public sealed class OpcUaTestProxy : IAsyncDisposable
{
    // OPC UA TCP "too busy" status, the answer to a connection attempt while the proxy is blocked.
    private const uint BadTcpServerTooBusy = 0x807D0000;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _targetPort;
    private readonly CancellationTokenSource _cancellation = new();

    // Written only by the accept loop and read after it ends.
    private readonly List<Task> _connections = [];
    private readonly Task _acceptLoop;

    private volatile bool _isBlocked;
    private int _generation;
    private int _rejectedConnectionCount;
    private int _disposed;

    /// <param name="targetPort">The port of the server on localhost.</param>
    public OpcUaTestProxy(int targetPort)
    {
        _targetPort = targetPort;
        _listener.Start();
        Url = $"opc.tcp://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>
    /// Gets the URL clients connect to. Uses 127.0.0.1 rather than localhost, so a server listening on
    /// localhost matches it to an alternate base address and advertises the proxy back to the client.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Gets how many new connections were rejected while blocked.
    /// </summary>
    public int RejectedConnectionCount => Volatile.Read(ref _rejectedConnectionCount);

    /// <summary>
    /// Drops all traffic on open connections without closing them, so the client detects the outage
    /// through its keep-alive rather than a socket error, and answers every new connection with an
    /// OPC UA "too busy" error. Connections open at that point never forward again, because resuming
    /// them after <see cref="Unblock"/> would leave a gap in their byte stream.
    /// </summary>
    public void Block()
    {
        // Blocked before the generation moves, so a connection accepted from here on is rejected rather than
        // forwarded under the new generation.
        _isBlocked = true;
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// Forwards new connections again. Connections that were open during the block stay silent.
    /// </summary>
    public void Unblock() => _isBlocked = false;

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            }
            catch (Exception)
            {
                // Canceled on dispose. Any other failure also ends accepting, and the test then times
                // out waiting for the client rather than failing inside the proxy.
                return;
            }

            // The generation is read before the blocked flag, so a connection forwarded here cannot carry the
            // generation of a block that started after it was accepted.
            var generation = Volatile.Read(ref _generation);
            _connections.Add(_isBlocked ? RejectAsync(client) : ForwardAsync(client, generation));
        }
    }

    private async Task RejectAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // Read the client's Hello so the error answers it rather than racing it.
                var header = new byte[8];
                await stream.ReadExactlyAsync(header, _cancellation.Token);
                var remaining = new byte[Math.Max(0, (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) - 8)];
                await stream.ReadExactlyAsync(remaining, _cancellation.Token);

                var error = new byte[16];
                "ERRF"u8.CopyTo(error);
                BinaryPrimitives.WriteUInt32LittleEndian(error.AsSpan(4), (uint)error.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(error.AsSpan(8), BadTcpServerTooBusy);
                BinaryPrimitives.WriteInt32LittleEndian(error.AsSpan(12), -1);
                await stream.WriteAsync(error, _cancellation.Token);
                Interlocked.Increment(ref _rejectedConnectionCount);

                // Let the client close first, so it reads the error instead of a reset connection.
                var buffer = new byte[256];
                while (await stream.ReadAsync(buffer, _cancellation.Token) > 0)
                {
                }
            }
            catch (Exception)
            {
                // The client went away or the proxy is shutting down.
            }
        }
    }

    private async Task ForwardAsync(TcpClient client, int generation)
    {
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                await server.ConnectAsync("localhost", _targetPort, _cancellation.Token);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                var clientToServer = PumpAsync(client.GetStream(), server.GetStream(), generation, connection.Token);
                var serverToClient = PumpAsync(server.GetStream(), client.GetStream(), generation, connection.Token);
                await Task.WhenAny(clientToServer, serverToClient);
                await connection.CancelAsync();
                await Task.WhenAll(clientToServer, serverToClient);
            }
            catch (Exception)
            {
                // Either side went away or the proxy is shutting down.
            }
        }
    }

    private async Task PumpAsync(NetworkStream source, NetworkStream destination, int generation, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }
        catch (Exception)
        {
            // Ends the connection; ForwardAsync closes both sides.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _cancellation.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        await Task.WhenAll(_connections);
        _cancellation.Dispose();
    }
}
