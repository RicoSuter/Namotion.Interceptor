using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeDiscoveryTests
{
    private static MulticastDnsServiceInstance Announcement(string? bridgeId, string instanceName = "Philips Hue - 1A2B3C") => new(
        instanceName,
        "001788fffe1a2b3c.local",
        [IPAddress.Parse("192.0.2.20")],
        bridgeId is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["bridgeid"] = bridgeId });

    [Fact]
    public async Task WhenABridgeIsAnnounced_ThenItIsFoundAndMatchedToItsSubject()
    {
        // Arrange
        var browser = new AnsweringBrowser(Announcement("001788FFFE1A2B3C"), Announcement(null));
        var existing = new HueBridge(NullLogger<HueBridge>.Instance) { BridgeId = "001788fffe1a2b3c" };
        ISubjectDiscovery discovery = new HueBridgeDiscovery(browser);

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(results);
        var bridge = Assert.IsType<HueDiscoveredBridge>(single);
        Assert.Equal("001788fffe1a2b3c", bridge.BridgeId);
        Assert.Equal("192.0.2.20", bridge.HostAddress);
        Assert.Equal("Philips Hue - 1A2B3C", bridge.DisplayName);
        Assert.True(single.Matches(existing));
        Assert.Equal([HueBridgeDiscovery.ServiceType], browser.ServiceTypes);
    }

    [Fact]
    public async Task WhenTheSubjectHasAnotherBridgeId_ThenTheBridgeDoesNotMatchIt()
    {
        // Arrange
        var browser = new AnsweringBrowser(Announcement("001788FFFE1A2B3C"));
        var existing = new HueBridge(NullLogger<HueBridge>.Instance) { BridgeId = "001788fffe999999" };
        ISubjectDiscovery discovery = new HueBridgeDiscovery(browser);

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(results);
        Assert.Equal("001788fffe1a2b3c", Assert.IsType<HueDiscoveredBridge>(single).BridgeId);
        Assert.False(single.Matches(existing));
    }

    [Fact]
    public async Task WhenTheInstanceNameIsBlank_ThenTheBridgeIdIsTheDisplayName()
    {
        // Arrange
        var browser = new AnsweringBrowser(Announcement("001788FFFE1A2B3C", instanceName: " "));
        ISubjectDiscovery discovery = new HueBridgeDiscovery(browser);

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(results);
        Assert.Equal("001788fffe1a2b3c", single.DisplayName);
    }

    private sealed class AnsweringBrowser(params MulticastDnsServiceInstance[] instances) : IMulticastDnsBrowser
    {
        public List<string> ServiceTypes { get; } = [];

        public Task<IReadOnlyList<MulticastDnsServiceInstance>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
        {
            ServiceTypes.Add(serviceType);
            return Task.FromResult<IReadOnlyList<MulticastDnsServiceInstance>>(instances);
        }
    }
}
