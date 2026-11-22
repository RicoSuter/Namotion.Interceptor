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
}
