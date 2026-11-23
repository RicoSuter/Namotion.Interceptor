using System.Net;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.MyStrom.Model;
using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.MyStrom.Tests;

public class MyStromSwitchDiscoveryTests
{
    private const string MacAddress = "FCF5C418ADAC";
    private const string HostAddress = "192.168.1.59";

    private static UdpDatagram Announcement(string macAddress = MacAddress, string ipAddress = HostAddress, byte deviceType = 0x6A) =>
        new((byte[])[.. Convert.FromHexString(macAddress), deviceType, 0x06], IPAddress.Parse(ipAddress));

    private static MyStromDiscoveredSwitch Found(string macAddress = MacAddress, string hostAddress = HostAddress) =>
        new(macAddress, hostAddress, "WS2");

    private static MyStromSwitch CreateSwitch(string? hostAddress = null, string? macAddress = null) =>
        new(new TestHttpClientFactory(), NullLogger<MyStromSwitch>.Instance)
        {
            HostAddress = hostAddress,
            Information = macAddress is null ? null : new MyStromSwitchInformation { Mac = macAddress }
        };

    private static async Task<List<IDiscoveryResult>> DiscoverAsync(FakeUdpDiscovery udpDiscovery)
    {
        ISubjectDiscovery discovery = new MyStromSwitchDiscovery(udpDiscovery);
        return await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();
    }

    [Fact]
    public async Task WhenSwitchesAreSearched_ThenTheAnnouncementPortIsListenedTo()
    {
        // Arrange
        var udpDiscovery = new FakeUdpDiscovery();

        // Act
        await DiscoverAsync(udpDiscovery);

        // Assert
        Assert.Equal([7979], udpDiscovery.ListenedPorts);
    }

    [Fact]
    public async Task WhenADatagramIsNoAnnouncementOfASupportedSwitch_ThenItIsSkipped()
    {
        // Arrange
        var udpDiscovery = new FakeUdpDiscovery(
            Announcement(macAddress: "FCF5C4000001", deviceType: 0x66),
            new UdpDatagram(new byte[9], IPAddress.Parse("192.168.1.61")),
            Announcement());

        // Act
        var discovered = await DiscoverAsync(udpDiscovery);

        // Assert
        var result = Assert.IsType<MyStromDiscoveredSwitch>(Assert.Single(discovered));
        Assert.Equal(MacAddress, result.MacAddress);
    }

    [Fact]
    public async Task WhenASwitchAnnouncesItselfTwice_ThenBothAnnouncementsHaveItsKey()
    {
        // Arrange
        var udpDiscovery = new FakeUdpDiscovery(
            Announcement(),
            Announcement(macAddress: "FCF5C4000001", ipAddress: "192.168.1.60", deviceType: 0x6B),
            Announcement());

        // Act
        var discovered = await DiscoverAsync(udpDiscovery);

        // Assert - the scan lists a key once.
        Assert.Equal([MacAddress, "FCF5C4000001", MacAddress], discovered.Select(result => result.Key));
        var result = Assert.IsType<MyStromDiscoveredSwitch>(discovered[0]);
        Assert.Equal(new MyStromDiscoveredSwitch(MacAddress, HostAddress, "WS2"), result);
        Assert.Equal(MacAddress, result.Key);
        Assert.Equal("myStrom Switch 18ADAC", result.DisplayName);
        Assert.Equal("WS2, 192.168.1.59", result.Detail);

        var other = Assert.IsType<MyStromDiscoveredSwitch>(discovered[1]);
        Assert.Equal("myStrom Switch 000001", other.DisplayName);
        Assert.Equal("WSE, 192.168.1.60", other.Detail);
    }

    [Fact]
    public async Task WhenASwitchIsAlreadyAdded_ThenItsResultMatchesOnlyItsSubject()
    {
        // Arrange
        var other = CreateSwitch(hostAddress: "192.168.1.10", macAddress: "FCF5C4000002");
        var existing = CreateSwitch(hostAddress: "kitchen.local", macAddress: MacAddress);

        // Act
        var result = Assert.Single(await DiscoverAsync(new FakeUdpDiscovery(Announcement())));

        // Assert
        Assert.False(result.Matches(other));
        Assert.True(result.Matches(existing));
    }

    [Theory]
    [InlineData("FCF5C418ADAC")]
    [InlineData("fcf5c418adac")]
    [InlineData("FC:F5:C4:18:AD:AC")]
    [InlineData("FC-F5-C4-18-AD-AC")]
    public void WhenTheSwitchReportsTheSameMacAddress_ThenItMatches(string macAddress)
    {
        // Arrange
        var existing = CreateSwitch(hostAddress: "192.168.1.10", macAddress: macAddress);

        // Act
        var matches = Found().Matches(existing);

        // Assert
        Assert.True(matches);
    }

    [Theory]
    [InlineData("192.168.1.59")]
    [InlineData(" 192.168.1.59 ")]
    public void WhenTheSwitchIsConfiguredWithTheSameHostAddress_ThenItMatches(string hostAddress)
    {
        // Arrange
        var existing = CreateSwitch(hostAddress: hostAddress);

        // Act
        var matches = Found().Matches(existing);

        // Assert
        Assert.True(matches);
    }

    [Theory]
    [InlineData("192.168.1.10", "FCF5C418ADAD")]
    [InlineData("192.168.1.10", "FCF5C418AD")]
    [InlineData("192.168.1.10", "FCF5C418ADAC00")]
    [InlineData("192.168.1.10", null)]
    [InlineData(null, null)]
    [InlineData(null, "")]
    public void WhenNeitherMacAddressNorHostAddressAreTheSame_ThenItDoesNotMatch(string? hostAddress, string? macAddress)
    {
        // Arrange
        var existing = CreateSwitch(hostAddress: hostAddress, macAddress: macAddress);

        // Act
        var matches = Found().Matches(existing);

        // Assert
        Assert.False(matches);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// Yields the datagrams it was created with to every listener and records the ports listened to.
    /// </summary>
    private sealed class FakeUdpDiscovery(params UdpDatagram[] datagrams) : IUdpDiscovery
    {
        public List<int> ListenedPorts { get; } = [];

        public IAsyncEnumerable<UdpDatagram> BroadcastAsync(int port, ReadOnlyMemory<byte> request, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<UdpDatagram> ListenAsync(int port, TimeSpan window, CancellationToken cancellationToken)
        {
            ListenedPorts.Add(port);
            return YieldAsync(cancellationToken);
        }

        private async IAsyncEnumerable<UdpDatagram> YieldAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var datagram in datagrams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return datagram;
            }
        }
    }
}
