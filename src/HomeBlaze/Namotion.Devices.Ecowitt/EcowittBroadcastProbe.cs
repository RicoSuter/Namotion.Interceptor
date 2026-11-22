using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Ecowitt;

/// <summary>
/// Asks the gateways on the local network to identify themselves with the broadcast request of the Ecowitt local API.
/// </summary>
internal static class EcowittBroadcastProbe
{
    internal static readonly byte[] Request = [0xFF, 0xFF, EcowittBroadcastReply.BroadcastCommand, 0x00, 0x04, 0x16];

    private static readonly IPEndPoint BroadcastEndPoint = new(IPAddress.Broadcast, 46000);
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(3);
    private const int MaximumDatagrams = 64;

    // Larger than the longest valid reply (274 bytes, with a name of 255 bytes), so a cut datagram is never valid.
    private const int ReceiveBufferLength = 512;

    /// <summary>
    /// Broadcasts the request, repeats it once against packet loss, and yields each valid reply that arrives within
    /// a few seconds, as it arrives. A gateway that receives both requests replies twice. Yields nothing instead of
    /// throwing when the request cannot be sent.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async IAsyncEnumerable<EcowittBroadcastReply> ProbeAsync(
        ILogger logger, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var socket = await OpenAsync(logger, cancellationToken);
        if (socket is null)
        {
            yield break;
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(ListeningWindow);
        _ = RepeatRequestAsync(socket, window.Token);

        var buffer = new byte[ReceiveBufferLength];
        for (var datagramCount = 0; datagramCount < MaximumDatagrams; datagramCount++)
        {
            var length = await ReceiveAsync(socket, buffer, logger, window.Token);
            if (length < 0)
            {
                break;
            }

            var reply = EcowittBroadcastReply.TryParse(buffer.AsSpan(0, length));
            if (reply is not null)
            {
                yield return reply;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <returns>A socket that sent the request, or null when it could not be sent.</returns>
    private static async Task<Socket?> OpenAsync(ILogger logger, CancellationToken cancellationToken)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            await socket.SendToAsync(Request, SocketFlags.None, BroadcastEndPoint, cancellationToken);
            return socket;
        }
        catch (SocketException exception)
        {
            socket?.Dispose();
            logger.LogDebug(exception, "The Ecowitt broadcast request could not be sent.");
            return null;
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    private static async Task RepeatRequestAsync(Socket socket, CancellationToken cancellationToken)
    {
        // Runs unobserved next to the receive loop, which disposes the socket when it ends, so every failure ends it
        // quietly.
        try
        {
            await Task.Delay(RepeatDelay, cancellationToken);
            await socket.SendToAsync(Request, SocketFlags.None, BroadcastEndPoint, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <returns>The length of the received datagram, 0 for one too large for the buffer, or -1 when receiving ended.</returns>
    private static async Task<int> ReceiveAsync(Socket socket, byte[] buffer, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The listening window ended, or the caller canceled, which ProbeAsync rethrows.
            return -1;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize)
        {
            return 0;
        }
        catch (SocketException exception)
        {
            logger.LogDebug(exception, "Receiving the replies to the Ecowitt broadcast request failed.");
            return -1;
        }
    }
}
