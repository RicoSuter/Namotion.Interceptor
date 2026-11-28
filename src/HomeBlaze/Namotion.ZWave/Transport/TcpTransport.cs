using System.Net.Sockets;

namespace Namotion.ZWave.Transport;

/// <summary>
/// Connects to a controller whose serial port a network bridge, such as ser2net, exposes on a TCP port.
/// TCP keepalive ends the connection about a minute after the bridge stops answering without closing it.
/// </summary>
public sealed class TcpTransport : IZWaveTransport
{
    // A bridge that disappears without closing the connection is detected after about a minute (30 s + 3 × 10 s).
    private const int KeepAliveTimeSeconds = 30;
    private const int KeepAliveIntervalSeconds = 10;
    private const int KeepAliveRetryCount = 3;

    public TcpTransport(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Host = host;
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }

    /// <summary>
    /// Returns the endpoint, such as "TCP 192.168.1.10:4001".
    /// </summary>
    public override string ToString() => $"TCP {Host}:{Port}";

    public async ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            EnableKeepAlive(socket);
            await socket.ConnectAsync(Host, Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void EnableKeepAlive(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        // Linux and macOS support every option; Windows supports the retry count from version 1703 and the times from 1709.
        if (!OperatingSystem.IsWindows() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveTimeSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSeconds);
        }

        if (!OperatingSystem.IsWindows() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveRetryCount);
        }
    }
}
