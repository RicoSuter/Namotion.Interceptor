using System.Net.Sockets;

namespace Namotion.ZWave.Transport;

/// <summary>
/// Connects to a controller whose serial port a network bridge, such as ser2net, exposes on a TCP port.
/// </summary>
public sealed class TcpTransport : IZWaveTransport
{
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

    public async ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(Host, Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
