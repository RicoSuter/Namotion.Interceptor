using System.Net;
using System.Net.Sockets;
using Namotion.ZWave.Transport;

namespace Namotion.ZWave.Tests.Transport;

public class TcpTransportTests
{
    [Fact]
    public async Task WhenOpened_ThenBytesFlowInBothDirections()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var transport = new TcpTransport("127.0.0.1", port);

        // Act
        var acceptTask = listener.AcceptTcpClientAsync();
        await using var stream = await transport.OpenAsync(CancellationToken.None);
        using var server = await acceptTask;
        await using var serverStream = server.GetStream();

        await stream.WriteAsync(new byte[] { 0x15 });
        var fromClient = new byte[1];
        await serverStream.ReadExactlyAsync(fromClient);

        await serverStream.WriteAsync(new byte[] { 0x06 });
        var fromServer = new byte[1];
        await stream.ReadExactlyAsync(fromServer);

        // Assert
        Assert.Equal(0x15, fromClient[0]);
        Assert.Equal(0x06, fromServer[0]);
    }

    [Fact]
    public async Task WhenOpened_ThenTcpKeepAliveIsEnabledWithShortProbes()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var transport = new TcpTransport("127.0.0.1", port);
        var acceptTask = listener.AcceptTcpClientAsync();

        // Act
        await using var stream = await transport.OpenAsync(CancellationToken.None);
        using var server = await acceptTask;

        // Assert
        var socket = Assert.IsType<NetworkStream>(stream).Socket;
        Assert.NotEqual(0, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        Assert.Equal(30, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
        Assert.Equal(10, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
        Assert.Equal(3, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
    }

    [Fact]
    public async Task WhenOpenedOnLinux_ThenTheTcpUserTimeoutIsOneMinute()
    {
        // TCP_USER_TIMEOUT exists on Linux only; the transport sets it nowhere else.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var transport = new TcpTransport("127.0.0.1", port);
        var acceptTask = listener.AcceptTcpClientAsync();

        // Act
        await using var stream = await transport.OpenAsync(CancellationToken.None);
        using var server = await acceptTask;

        // Assert
        var socket = Assert.IsType<NetworkStream>(stream).Socket;
        var value = new byte[sizeof(int)];
        socket.GetRawSocketOption(6, 18, value);
        Assert.Equal(60_000, BitConverter.ToInt32(value));
    }

    [Fact]
    public async Task WhenOpeningIsCanceled_ThenOperationCanceledIsThrown()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var transport = new TcpTransport("127.0.0.1", port);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.OpenAsync(cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData("", 4001)]
    [InlineData("host", 0)]
    [InlineData("host", 65536)]
    public void WhenArgumentsAreInvalid_ThenTheConstructorThrows(string host, int port)
    {
        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => new TcpTransport(host, port));
    }
}
