using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeDiscoveryTests
{
    private static MulticastDnsService Announcement(string? bridgeId, string instanceName = "Philips Hue - 1A2B3C") => new(
        instanceName,
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
        ISubjectDiscovery discovery = new HueBridgeDiscovery();
        var scan = new DiscoveryScan(new EmptySsdpSearch(), browser, new UnusedUdpDiscovery(), CancellationToken.None);

        // Act
        var discovered = await discovery.DiscoverAsync(scan, [existing], CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        var bridge = Assert.IsType<HueDiscoveredBridge>(single.Result);
        Assert.Equal("001788fffe1a2b3c", bridge.BridgeId);
        Assert.Equal("192.168.1.20", bridge.HostAddress);
        Assert.Equal("Philips Hue - 1A2B3C", bridge.DisplayName);
        Assert.Same(existing, single.ExistingSubject);
        Assert.Equal([HueBridgeDiscovery.ServiceType], browser.ServiceTypes);
    }

    [Fact]
    public async Task WhenNoSubjectHasTheBridgeId_ThenTheBridgeIsFoundWithoutASubject()
    {
        // Arrange
        var browser = new AnsweringBrowser(Announcement("001788FFFE1A2B3C"));
        var existing = new HueBridge(NullLogger<HueBridge>.Instance) { BridgeId = "001788fffe999999" };
        ISubjectDiscovery discovery = new HueBridgeDiscovery();
        var scan = new DiscoveryScan(new EmptySsdpSearch(), browser, new UnusedUdpDiscovery(), CancellationToken.None);

        // Act
        var discovered = await discovery.DiscoverAsync(scan, [existing], CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        Assert.Equal("001788fffe1a2b3c", Assert.IsType<HueDiscoveredBridge>(single.Result).BridgeId);
        Assert.Null(single.ExistingSubject);
    }

    [Fact]
    public async Task WhenTheInstanceNameIsBlank_ThenTheBridgeIdIsTheDisplayName()
    {
        // Arrange
        var browser = new AnsweringBrowser(Announcement("001788FFFE1A2B3C", instanceName: " "));
        ISubjectDiscovery discovery = new HueBridgeDiscovery();
        var scan = new DiscoveryScan(new EmptySsdpSearch(), browser, new UnusedUdpDiscovery(), CancellationToken.None);

        // Act
        var discovered = await discovery.DiscoverAsync(scan, [], CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        Assert.Equal("001788fffe1a2b3c", single.Result.DisplayName);
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

    private sealed class UnusedUdpDiscovery : IUdpDiscovery
    {
        public IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
