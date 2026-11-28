using System.Net.Sockets;

namespace Namotion.ZWave.Transport;

/// <summary>
/// Connects to a controller whose serial port a network bridge, such as ser2net, exposes on a TCP port.
/// A connect that does not complete within 10 s fails with a <see cref="TimeoutException"/>. A bridge that stops
/// answering without closing the connection ends it after about a minute: through TCP keepalive while the connection
/// is idle, and on Linux also through <c>TCP_USER_TIMEOUT</c> while sent data stays unacknowledged.
/// </summary>
public sealed class TcpTransport : IZWaveTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    // An idle connection to a bridge that disappeared ends after about a minute (30 s + 3 × 10 s).
    private const int KeepAliveTimeSeconds = 30;
    private const int KeepAliveIntervalSeconds = 10;
    private const int KeepAliveRetryCount = 3;

    // Keepalive probes only an idle connection; this ends one whose sent data stays unacknowledged (IPPROTO_TCP, TCP_USER_TIMEOUT).
    private const int UserTimeoutMilliseconds = 60_000;
    private const int LinuxTcpProtocolLevel = 6;
    private const int LinuxTcpUserTimeoutOption = 18;

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

    /// <inheritdoc />
    /// <exception cref="TimeoutException">The connection was not established within 10 s.</exception>
    public async ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            EnableDeadBridgeDetection(socket);
            await ConnectAsync(socket, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ConnectAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Connecting to {Host}:{Port} did not complete within {ConnectTimeout.TotalSeconds} s.", exception);
        }
    }

    private static void EnableDeadBridgeDetection(Socket socket)
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

        if (OperatingSystem.IsLinux())
        {
            Span<byte> userTimeout = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(userTimeout, UserTimeoutMilliseconds);
            socket.SetRawSocketOption(LinuxTcpProtocolLevel, LinuxTcpUserTimeoutOption, userTimeout);
        }
    }
}
