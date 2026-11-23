using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Discovery;

/// <summary>
/// Exchanges UDP datagrams over IPv4. A broadcast is sent to the broadcast address of the subnet of each adapter that
/// <see cref="NetworkAdapters"/> lists, so it leaves on every adapter rather than only on that of the default route,
/// or to <see cref="IPAddress.Broadcast"/> when no listed adapter has a subnet broadcast address.
/// </summary>
internal sealed class UdpDiscovery : IUdpDiscovery
{
    internal const int MaximumDatagrams = 512;
    internal const int MaximumDatagramLength = 1024;
    internal static readonly TimeSpan MaximumWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RepeatDelay = TimeSpan.FromSeconds(1);
    private static readonly IPEndPoint AnyEndPoint = new(IPAddress.Any, 0);

    // Received into a buffer one byte longer than the limit: where the platform cuts an overlong datagram to the buffer
    // instead of failing the receive, the cut datagram still shows as overlong.
    private const int ReceiveBufferLength = MaximumDatagramLength + 1;

    private readonly ILogger<UdpDiscovery> _logger;

    public UdpDiscovery(ILogger<UdpDiscovery> logger)
    {
        _logger = logger;
    }

    public IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken)
    {
        ValidatePort(port);
        ValidateWindow(window);
        if (request.Length is 0 or > MaximumDatagramLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.Length, $"The request must be from 1 to {MaximumDatagramLength} bytes long.");
        }

        return BroadcastCoreAsync(port, request.ToArray(), window, cancellationToken);
    }

    public IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken)
    {
        ValidatePort(port);
        ValidateWindow(window);
        return ListenCoreAsync(port, window, cancellationToken);
    }

    private async IAsyncEnumerable<UdpDatagram> BroadcastCoreAsync(
        int port, byte[] request, TimeSpan window, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var broadcastAddresses = NetworkAdapters.GetIPv4BroadcastAddresses();
        IReadOnlyList<IPEndPoint> endPoints = broadcastAddresses.Count == 0
            ? [new IPEndPoint(IPAddress.Broadcast, port)]
            : [.. broadcastAddresses.Select(address => new IPEndPoint(address, port))];

        using var socket = await OpenBroadcastSocketAsync(request, endPoints, port, cancellationToken);
        if (socket is null)
        {
            yield break;
        }

        using var windowSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowSource.CancelAfter(window);
        try
        {
            _ = RepeatRequestAsync(socket, request, endPoints, windowSource.Token);
            await foreach (var datagram in ReceiveAsync(socket, port, windowSource.Token))
            {
                yield return datagram;
            }
        }
        finally
        {
            // Stops the repeated request also when the caller stops early; it tolerates the disposal of the socket.
            await windowSource.CancelAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async IAsyncEnumerable<UdpDatagram> ListenCoreAsync(
        int port, TimeSpan window, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var socket = OpenListeningSocket(port);
        if (socket is null)
        {
            yield break;
        }

        using var windowSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowSource.CancelAfter(window);
        await foreach (var datagram in ReceiveAsync(socket, port, windowSource.Token))
        {
            yield return datagram;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <returns>A socket that sent the request to at least one end point, or null when it could not be sent.</returns>
    private async Task<Socket?> OpenBroadcastSocketAsync(
        byte[] request, IReadOnlyList<IPEndPoint> endPoints, int port, CancellationToken cancellationToken)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
            socket.Bind(AnyEndPoint);

            var failure = await SendToAllAsync(socket, request, endPoints, cancellationToken);
            if (failure is null)
            {
                return socket;
            }

            socket.Dispose();
            _logger.LogDebug(failure, "The UDP broadcast to port {Port} could not be sent.", port);
            return null;
        }
        catch (SocketException exception)
        {
            socket?.Dispose();
            _logger.LogDebug(exception, "The UDP broadcast to port {Port} could not be sent.", port);
            return null;
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    /// <returns>A socket bound to <paramref name="port"/>, or null when it could not be bound.</returns>
    private Socket? OpenListeningSocket(int port)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Takes effect only when set before binding.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
        catch (SocketException exception)
        {
            socket?.Dispose();
            _logger.LogDebug(exception, "The UDP port {Port} could not be bound.", port);
            return null;
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    private static async Task RepeatRequestAsync(
        Socket socket, byte[] request, IReadOnlyList<IPEndPoint> endPoints, CancellationToken cancellationToken)
    {
        // Runs unobserved next to the receive loop, which disposes the socket when it ends, so every failure ends it
        // quietly.
        try
        {
            await Task.Delay(RepeatDelay, cancellationToken);
            await SendToAllAsync(socket, request, endPoints, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <returns>Null when the request was sent to at least one end point, otherwise the first failure.</returns>
    private static async Task<SocketException?> SendToAllAsync(
        Socket socket, byte[] request, IReadOnlyList<IPEndPoint> endPoints, CancellationToken cancellationToken)
    {
        SocketException? firstFailure = null;
        var sentCount = 0;
        foreach (var endPoint in endPoints)
        {
            try
            {
                await socket.SendToAsync(request, SocketFlags.None, endPoint, cancellationToken);
                sentCount++;
            }
            catch (SocketException exception)
            {
                // Another adapter can still send.
                firstFailure ??= exception;
            }
        }

        return sentCount > 0 ? null : firstFailure;
    }

    private async IAsyncEnumerable<UdpDatagram> ReceiveAsync(
        Socket socket, int port, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Every datagram is copied out before it is yielded, so the pooled buffer never leaves this method.
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferLength);
        try
        {
            var senderAddress = new SocketAddress(AddressFamily.InterNetwork);
            for (var datagramCount = 0; datagramCount < MaximumDatagrams; datagramCount++)
            {
                var length = await ReceiveOneAsync(socket, buffer, senderAddress, port, cancellationToken);
                if (length < 0)
                {
                    break;
                }

                if (length <= MaximumDatagramLength)
                {
                    var sender = ((IPEndPoint)AnyEndPoint.Create(senderAddress)).Address;
                    yield return new UdpDatagram(buffer.AsSpan(0, length).ToArray(), sender);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <returns>
    /// The length of the received datagram, more than <see cref="MaximumDatagramLength"/> for one to skip, or -1 when
    /// receiving ended.
    /// </returns>
    private async Task<int> ReceiveOneAsync(
        Socket socket, byte[] buffer, SocketAddress senderAddress, int port, CancellationToken cancellationToken)
    {
        try
        {
            return await socket.ReceiveFromAsync(buffer.AsMemory(0, ReceiveBufferLength), SocketFlags.None, senderAddress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The window ended, or the caller canceled, which the public enumerations rethrow.
            return -1;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
        {
            // An overlong datagram, or on Windows the ICMP answer to an earlier send; neither ends the exchange.
            return ReceiveBufferLength;
        }
        catch (SocketException exception)
        {
            _logger.LogDebug(exception, "Receiving the UDP datagrams for port {Port} failed.", port);
            return -1;
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, $"The port must be from 1 to {IPEndPoint.MaxPort}.");
        }
    }

    private static void ValidateWindow(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window, MaximumWindow);
    }
}
