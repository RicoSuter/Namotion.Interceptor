namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Exchanges UDP datagrams with the devices on the local network over IPv4. Each call is its own exchange on its own
/// socket.
/// </summary>
/// <remarks>
/// A call reads at most 512 datagrams and yields only those of at most 1024 bytes; longer ones count towards that
/// limit but are skipped. A socket error during the exchange ends the call without an exception, logged at debug
/// level, so a broadcast from a host where it cannot be sent yields nothing. Only a listening socket that
/// <see cref="ListenAsync"/> could not open or bind throws. The end of the window ends the enumeration normally, while a
/// cancellation of the caller's token throws.
/// </remarks>
public interface IUdpDiscovery
{
    /// <summary>
    /// Sends <paramref name="request"/> as a broadcast to <paramref name="port"/> on every IPv4 adapter, repeats it once
    /// after about a second against packet loss, and yields each datagram that arrives at the sending socket within
    /// <paramref name="window"/>, as it arrives. A device that receives both requests answers twice.
    /// </summary>
    /// <param name="port">The port the devices listen on, from 1 to 65535.</param>
    /// <param name="request">The request, from 1 to 1024 bytes; it is copied, so the caller can reuse its memory.</param>
    /// <param name="window">How long to wait for answers, more than zero and at most 30 seconds.</param>
    /// <param name="cancellationToken">Ends the enumeration with an <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range; thrown by the call itself.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>
    /// Binds <paramref name="port"/> on every IPv4 address with address reuse and yields each datagram that arrives
    /// within <paramref name="window"/>, as it arrives. Whether the port is shared depends on the platform: on Linux,
    /// concurrent calls and other listeners that also use address reuse share it, while a listener that holds it
    /// without address reuse makes the bind fail; on Windows, address reuse binds over a listener that does not
    /// demand exclusive use.
    /// </summary>
    /// <param name="port">The port to listen on, from 1 to 65535.</param>
    /// <param name="window">How long to listen, more than zero and at most 30 seconds.</param>
    /// <param name="cancellationToken">Ends the enumeration with an <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range; thrown by the call itself.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">The listening socket could not be opened or bound to the port; thrown by the enumeration.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken);
}
