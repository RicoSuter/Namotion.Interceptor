using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Forwards TCP connections to an OPC UA server and can make the server unreachable while it keeps its
/// sessions and subscriptions, which a server restart cannot.
/// </summary>
public sealed class OpcUaTestProxy : IAsyncDisposable
{
    // OPC UA TCP "too busy" status. The SDK reconnect handler does not retry reactivating the session on
    // it, so a client that hits it while blocked recreates its session and transfers its subscriptions.
    private const uint BadTcpServerTooBusy = 0x807D0000;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly Task _acceptLoop;

    private volatile bool _isBlocked;
    private int _rejectedConnectionCount;

    public OpcUaTestProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>
    /// Gets the URL clients connect to. Uses 127.0.0.1 rather than localhost, so a server listening on
    /// localhost matches it to an alternate base address and advertises the proxy back to the client.
    /// </summary>
    public string Url => $"opc.tcp://127.0.0.1:{Port}";

    /// <summary>
    /// Gets how many new connections were rejected while blocked.
    /// </summary>
    public int RejectedConnectionCount => Volatile.Read(ref _rejectedConnectionCount);

    /// <summary>
    /// Drops all traffic on open connections without closing them, so the client detects the outage
    /// through its keep-alive rather than a socket error, and answers every new connection with an
    /// OPC UA "too busy" error.
    /// </summary>
    public void Block() => _isBlocked = true;

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
            catch (Exception) when (_cancellation.IsCancellationRequested)
            {
                return;
            }

            Track(_isBlocked ? RejectAsync(client) : ForwardAsync(client));
        }
    }

    private void Track(Task connection)
    {
        _connections.TryAdd(connection, 0);
        connection.ContinueWith(
            static (task, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(task, out _),
            _connections,
            TaskScheduler.Default);
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
                var remaining = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) - 8];
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

    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        {
            try
            {
                await server.ConnectAsync(_targetHost, _targetPort, _cancellation.Token);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                var clientToServer = PumpAsync(client.GetStream(), server.GetStream(), connection.Token);
                var serverToClient = PumpAsync(server.GetStream(), client.GetStream(), connection.Token);
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

    private async Task PumpAsync(NetworkStream source, NetworkStream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (!_isBlocked)
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
        await _cancellation.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        await Task.WhenAll(_connections.Keys);
        _cancellation.Dispose();
    }
}
