using System.Net;
using System.Net.Sockets;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class UdpDiscoveryTests
{
    private static readonly byte[] Request = [0x01];

    private static UdpDiscovery CreateDiscovery() => new(NullLogger<UdpDiscovery>.Instance);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void WhenThePortIsOutOfRange_ThenTheCallThrows(int port)
    {
        // Arrange
        var discovery = CreateDiscovery();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>("port", () => discovery.BroadcastAsync(port, Request, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>("port", () => discovery.ListenAsync(port, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(30_001)]
    public void WhenTheWindowIsOutOfRange_ThenTheCallThrows(int milliseconds)
    {
        // Arrange
        var discovery = CreateDiscovery();
        var window = TimeSpan.FromMilliseconds(milliseconds);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>("window", () => discovery.BroadcastAsync(46000, Request, window, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>("window", () => discovery.ListenAsync(7979, window, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(UdpDiscovery.MaximumDatagramLength + 1)]
    public void WhenTheRequestIsEmptyOrTooLong_ThenTheCallThrows(int length)
    {
        // Arrange
        var discovery = CreateDiscovery();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            "request", () => discovery.BroadcastAsync(46000, new byte[length], TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheCallerHasCanceled_ThenTheEnumerationThrows()
    {
        // Arrange
        var discovery = CreateDiscovery();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in discovery.BroadcastAsync(46000, Request, TimeSpan.FromSeconds(1), cancellation.Token))
            {
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in discovery.ListenAsync(7979, TimeSpan.FromSeconds(1), cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public async Task WhenADatagramIsLongerThanTheLimit_ThenItIsSkippedAndTheNextIsYielded()
    {
        // Arrange
        var port = GetFreePort();
        await using var enumerator = CreateDiscovery().ListenAsync(port, UdpDiscovery.MaximumWindow, CancellationToken.None).GetAsyncEnumerator();

        // Act
        var moveNext = enumerator.MoveNextAsync();
        Send(port, new byte[UdpDiscovery.MaximumDatagramLength * 2]);
        Send(port, new byte[UdpDiscovery.MaximumDatagramLength + 1]);
        Send(port, [1, 2, 3]);
        var hasDatagram = await moveNext.AsTask().WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.True(hasDatagram);
        Assert.Equal([1, 2, 3], enumerator.Current.Payload.ToArray());
    }

    [Fact]
    public async Task WhenAnEmptyDatagramArrives_ThenItIsYieldedWithItsSenderAddress()
    {
        // Arrange
        var port = GetFreePort();
        await using var enumerator = CreateDiscovery().ListenAsync(port, UdpDiscovery.MaximumWindow, CancellationToken.None).GetAsyncEnumerator();

        // Act
        var moveNext = enumerator.MoveNextAsync();
        Send(port, []);
        var hasDatagram = await moveNext.AsTask().WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.True(hasDatagram);
        Assert.Equal(0, enumerator.Current.Payload.Length);
        Assert.Equal(IPAddress.Loopback, enumerator.Current.SenderAddress);
    }

    [Fact]
    public async Task WhenTheWindowEnds_ThenTheEnumerationEndsNormally()
    {
        // Arrange
        var port = GetFreePort();

        // Act
        var datagrams = await CreateDiscovery().ListenAsync(port, TimeSpan.FromMilliseconds(50), CancellationToken.None)
            .ToListAsync().AsTask().WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Empty(datagrams);
    }

    [Fact]
    public async Task WhenTheCallerCancelsWhileReceiving_ThenTheEnumerationThrows()
    {
        // Arrange
        var port = GetFreePort();
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = CreateDiscovery().ListenAsync(port, UdpDiscovery.MaximumWindow, cancellation.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync();

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext.AsTask().WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenMoreDatagramsThanTheLimitArrive_ThenTheEnumerationEndsAtTheLimit()
    {
        // Arrange
        var port = GetFreePort();
        var count = 0;

        // Act
        var listening = Task.Run(async () =>
        {
            // Lockstep: each datagram is sent once the one before was received, so none overflows the receive buffer.
            await using var enumerator = CreateDiscovery().ListenAsync(port, UdpDiscovery.MaximumWindow, CancellationToken.None).GetAsyncEnumerator();
            var moveNext = enumerator.MoveNextAsync();
            Send(port, [0]);
            while (await moveNext)
            {
                count++;
                moveNext = enumerator.MoveNextAsync();
                Send(port, [0]);
            }
        });
        await listening.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(UdpDiscovery.MaximumDatagrams, count);
    }

    [Fact]
    public async Task WhenAnotherSocketHoldsThePortWithoutAddressReuse_ThenTheEnumerationThrows()
    {
        // Arrange
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Windows lets address reuse bind over a socket that does not demand exclusive use.
            holder.ExclusiveAddressUse = true;
        }

        holder.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)holder.LocalEndPoint!).Port;

        // Act & Assert
        await Assert.ThrowsAsync<SocketException>(async () =>
            await CreateDiscovery().ListenAsync(port, UdpDiscovery.MaximumWindow, CancellationToken.None)
                .ToListAsync().AsTask().WaitAsync(TestTimeouts.Default));
    }

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Sends a datagram to the loopback address. A listening enumeration has bound its socket once its first
    /// <c>MoveNextAsync</c> returned, as an async iterator runs synchronously up to its first pending receive.
    /// </summary>
    private static void Send(int port, byte[] payload)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SendTo(payload, new IPEndPoint(IPAddress.Loopback, port));
    }
}
