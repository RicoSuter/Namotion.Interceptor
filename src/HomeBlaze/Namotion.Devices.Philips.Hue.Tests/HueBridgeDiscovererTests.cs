using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeDiscovererTests
{
    private static MulticastDnsService Announcement(string? bridgeId) => new(
        "Philips Hue - 1A2B3C",
        "001788fffe1a2b3c.local",
        443,
        [IPAddress.Parse("192.168.1.20")],
        bridgeId is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["bridgeid"] = bridgeId });

    [Fact]
    public async Task WhenABridgeIsAnnounced_ThenItIsFoundAndMatchedToItsSubject()
    {
        // Arrange
        var browser = new AnsweringBrowser(Announcement("001788FFFE1A2B3C"), Announcement(null));
        var existing = new HueBridge(NullLogger<HueBridge>.Instance) { BridgeId = "001788fffe1a2b3c" };
        ISubjectDiscoverer discoverer = new HueBridgeDiscoverer();
        var scan = new DiscoveryScan(new EmptySsdpSearch(), browser, CancellationToken.None);

        // Act
        var discovered = await discoverer.DiscoverAsync(scan, [existing], CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        var bridge = Assert.IsType<HueDiscoveredBridge>(single.Result);
        Assert.Equal("001788fffe1a2b3c", bridge.BridgeId);
        Assert.Equal("192.168.1.20", bridge.HostAddress);
        Assert.Equal("Philips Hue - 1A2B3C", bridge.DisplayName);
        Assert.Same(existing, single.ExistingSubject);
        Assert.Equal([HueBridgeDiscoverer.ServiceType], browser.ServiceTypes);
    }

    private sealed class AnsweringBrowser(params MulticastDnsService[] services) : IMulticastDnsBrowser
    {
        public List<string> ServiceTypes { get; } = [];

        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
        {
            ServiceTypes.Add(serviceType);
            return Task.FromResult<IReadOnlyList<MulticastDnsService>>(services);
        }
    }

    private sealed class EmptySsdpSearch : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }
}
