using System.Net;
using System.Net.Sockets;
using System.Text;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeHttpClientTests
{
    [Fact]
    public async Task WhenTheServerKeepsTheConnectionAlive_ThenEachRequestStillOpensItsOwnConnection()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serverCancellation = new CancellationTokenSource();
        var connections = 0;
        var serverTask = Task.Run(async () =>
        {
            while (!serverCancellation.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(serverCancellation.Token);
                Interlocked.Increment(ref connections);
                _ = AnswerWithKeepAliveAsync(socket, serverCancellation.Token);
            }
        });

        using var httpClient = NukiBridgeHttpClient.Create();
        var client = new NukiBridgeClient(
            httpClient, "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, "123456", NukiTokenMode.Plain);

        // Act
        await client.GetListAsync(CancellationToken.None);
        await client.GetListAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, Volatile.Read(ref connections));

        await serverCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serverTask);
    }

    // Answers every request on the connection with a keep-alive response until the connection is closed.
    private static async Task AnswerWithKeepAliveAsync(TcpClient socket, CancellationToken cancellationToken)
    {
        using var _ = socket;
        var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var body = Encoding.UTF8.GetBytes(NukiFixtures.List);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n");
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is not null)
            {
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                {
                    // Skips the request headers.
                }

                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // The client or the test closed the connection.
        }
    }
}
