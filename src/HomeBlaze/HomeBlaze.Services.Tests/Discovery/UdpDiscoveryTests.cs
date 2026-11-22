using System.Net;
using HomeBlaze.Services.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData("192.168.1.216", "255.255.255.0", "192.168.1.255")]
    [InlineData("172.17.0.1", "255.255.0.0", "172.17.255.255")]
    [InlineData("10.0.0.5", "255.255.255.252", "10.0.0.7")]
    public void WhenTheSubnetHasHostAddresses_ThenItsBroadcastAddressIsComputed(string address, string mask, string expected)
    {
        // Act
        var broadcastAddress = NetworkAdapters.GetBroadcastAddress(IPAddress.Parse(address), IPAddress.Parse(mask));

        // Assert
        Assert.Equal(IPAddress.Parse(expected), broadcastAddress);
    }

    [Theory]
    [InlineData("10.8.0.2", "255.255.255.255")]
    [InlineData("10.8.0.2", "255.255.255.254")]
    [InlineData("fe80::1", "255.255.255.0")]
    public void WhenTheAddressIsOfAPointToPointLinkOrNotIPv4_ThenItHasNoBroadcastAddress(string address, string mask)
    {
        // Act
        var broadcastAddress = NetworkAdapters.GetBroadcastAddress(IPAddress.Parse(address), IPAddress.Parse(mask));

        // Assert
        Assert.Null(broadcastAddress);
    }
}
