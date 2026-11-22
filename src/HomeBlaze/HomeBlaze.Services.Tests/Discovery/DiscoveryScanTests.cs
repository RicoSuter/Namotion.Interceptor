using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Xunit;

namespace HomeBlaze.Services.Tests.Discovery;

public class DiscoveryScanTests
{
    private static readonly MulticastDnsService Bridge = new(
        "Philips Hue - 1A2B3C", "hue.local", 443, [IPAddress.Parse("192.168.1.20")], new Dictionary<string, string>());

    [Fact]
    public async Task WhenTwoDiscoverersBrowseTheSameServiceType_ThenOneQueryIsSent()
    {
        // Arrange
        var browser = new FakeMulticastDnsBrowser();
        var scan = new DiscoveryScan(new FakeSsdpSearch(), browser, CancellationToken.None);

        // Act
        var first = scan.MulticastDns.BrowseAsync("_hue._tcp", CancellationToken.None);
        var second = scan.MulticastDns.BrowseAsync("_HUE._tcp", CancellationToken.None);
        browser.Answer(Bridge);

        // Assert
        Assert.Same(await first, await second);
        Assert.Equal(1, browser.CallCount);
    }

    [Fact]
    public async Task WhenOneCallerStopsWaiting_ThenTheOtherStillGetsTheAnswer()
    {
        // Arrange
        var browser = new FakeMulticastDnsBrowser();
        var scan = new DiscoveryScan(new FakeSsdpSearch(), browser, CancellationToken.None);
        using var stoppedWaiting = new CancellationTokenSource();
        var first = scan.MulticastDns.BrowseAsync("_hue._tcp", stoppedWaiting.Token);
        var second = scan.MulticastDns.BrowseAsync("_hue._tcp", CancellationToken.None);

        // Act
        await stoppedWaiting.CancelAsync();
        browser.Answer(Bridge);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Same(Bridge, Assert.Single(await second));
    }
}
