using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.MyStrom;

/// <summary>
/// Listens for the announcements that myStrom devices broadcast to UDP port 7979.
/// </summary>
internal static class MyStromAnnouncementListener
{
    private const int Port = 7979;
    private const int MaximumDatagrams = 512;

    // Longer than an announcement, so a longer datagram that the receive cuts to the buffer is never taken for one.
    private const int ReceiveBufferLength = 16;

    // Longer than the announcement interval of about 5 seconds, so every device announces itself once.
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(6);

    private static readonly IPEndPoint AnyEndPoint = new(IPAddress.Any, 0);

    /// <summary>
    /// Yields each valid announcement of a supported switch that arrives within a few seconds, as it arrives. A switch
    /// that announces itself more than once in that time is yielded more than once. Yields nothing instead of throwing
    /// when the port cannot be bound.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async IAsyncEnumerable<MyStromAnnouncement> ListenAsync(
        ILogger logger, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var socket = Open(logger);
        if (socket is null)
        {
            yield break;
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(ListeningWindow);

        var buffer = new byte[ReceiveBufferLength];
        var remoteAddress = new SocketAddress(AddressFamily.InterNetwork);
        for (var datagramCount = 0; datagramCount < MaximumDatagrams; datagramCount++)
        {
            var length = await ReceiveAsync(socket, buffer, remoteAddress, logger, window.Token);
            if (length < 0)
            {
                break;
            }

            // Checked before the sender's address is created, so other datagrams cost no allocation.
            if (length == MyStromAnnouncement.Length)
            {
                var sourceAddress = ((IPEndPoint)AnyEndPoint.Create(remoteAddress)).Address;
                var announcement = MyStromAnnouncement.TryParse(buffer.AsSpan(0, length), sourceAddress);
                if (announcement is not null)
                {
                    yield return announcement;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <returns>A socket bound to the announcement port, or null when it could not be bound.</returns>
    private static Socket? Open(ILogger logger)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Set before binding, so concurrent scans and other listeners on the host share the port; each bound socket
            // receives every broadcast.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            return socket;
        }
        catch (SocketException exception)
        {
            socket?.Dispose();
            logger.LogDebug(exception, "The myStrom announcement port {Port} could not be bound.", Port);
            return null;
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    /// <returns>The length of the received datagram, 0 for one too large for the buffer, or -1 when receiving ended.</returns>
    private static async Task<int> ReceiveAsync(
        Socket socket, byte[] buffer, SocketAddress remoteAddress, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteAddress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The listening window ended, or the caller canceled, which ListenAsync rethrows.
            return -1;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize)
        {
            return 0;
        }
        catch (SocketException exception)
        {
            logger.LogDebug(exception, "Receiving myStrom announcements failed.");
            return -1;
        }
    }
}
