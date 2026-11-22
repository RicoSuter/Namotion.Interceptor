using System.Net;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.MyStrom.Model;
using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.MyStrom.Tests;

public class MyStromSwitchDiscovererTests
{
    private const string MacAddress = "FCF5C418ADAC";
    private const string HostAddress = "192.168.1.59";

    private static MyStromAnnouncement Announcement(string macAddress = MacAddress, string ipAddress = HostAddress, string deviceType = "WS2") =>
        new(macAddress, deviceType, IPAddress.Parse(ipAddress));

    private static MyStromDiscoveredSwitch Found(string macAddress = MacAddress, string hostAddress = HostAddress) =>
        new(macAddress, hostAddress, "WS2");

    private static MyStromSwitch CreateSwitch(string? hostAddress = null, string? macAddress = null) =>
        new(new TestHttpClientFactory(), NullLogger<MyStromSwitch>.Instance)
        {
            HostAddress = hostAddress,
            Information = macAddress is null ? null : new MyStromSwitchInformation { Mac = macAddress }
        };

    private static async Task<List<DiscoveredSubject>> DiscoverAsync(
        IReadOnlyList<MyStromAnnouncement> announcements, params IInterceptorSubject[] existingSubjects)
    {
        ISubjectDiscoverer discoverer = new MyStromSwitchDiscoverer(cancellationToken => ToAsyncEnumerable(announcements, cancellationToken));
        var scan = new DiscoveryScan(new UnusedSsdpSearch(), new UnusedMulticastDnsBrowser(), CancellationToken.None);

        var discovered = new List<DiscoveredSubject>();
        await foreach (var subject in discoverer.DiscoverAsync(scan, existingSubjects, CancellationToken.None))
        {
            discovered.Add(subject);
        }

        return discovered;
    }

    private static async IAsyncEnumerable<MyStromAnnouncement> ToAsyncEnumerable(
        IReadOnlyList<MyStromAnnouncement> announcements, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var announcement in announcements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return announcement;
        }
    }

    [Fact]
    public async Task WhenASwitchAnnouncesItselfTwice_ThenItIsFoundOnce()
    {
        // Arrange
        MyStromAnnouncement[] announcements =
        [
            Announcement(),
            Announcement(macAddress: "FCF5C4000001", ipAddress: "192.168.1.60", deviceType: "WSE"),
            Announcement()
        ];

        // Act
        var discovered = await DiscoverAsync(announcements);

        // Assert
        Assert.Equal(2, discovered.Count);
        var subject = discovered[0];
        Assert.Equal(typeof(MyStromSwitch), subject.SubjectType);
        Assert.Null(subject.ExistingSubject);
        var result = Assert.IsType<MyStromDiscoveredSwitch>(subject.Result);
        Assert.Equal(new MyStromDiscoveredSwitch(MacAddress, HostAddress, "WS2"), result);
        Assert.Equal(MacAddress, result.Key);
        Assert.Equal("myStrom Switch 18ADAC", result.DisplayName);
        Assert.Equal("WS2, 192.168.1.59", result.Detail);

        var other = Assert.IsType<MyStromDiscoveredSwitch>(discovered[1].Result);
        Assert.Equal("myStrom Switch 000001", other.DisplayName);
        Assert.Equal("WSE, 192.168.1.60", other.Detail);
    }

    [Fact]
    public async Task WhenASwitchIsAlreadyAdded_ThenItIsFoundWithItsSubject()
    {
        // Arrange
        var other = CreateSwitch(hostAddress: "192.168.1.10", macAddress: "FCF5C4000002");
        var existing = CreateSwitch(hostAddress: "kitchen.local", macAddress: MacAddress);

        // Act
        var discovered = await DiscoverAsync([Announcement()], other, existing);

        // Assert
        Assert.Same(existing, Assert.Single(discovered).ExistingSubject);
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
        var matches = MyStromSwitchDiscoverer.IsSameSwitch(existing, Found());

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
        var matches = MyStromSwitchDiscoverer.IsSameSwitch(existing, Found());

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
        var matches = MyStromSwitchDiscoverer.IsSameSwitch(existing, Found());

        // Assert
        Assert.False(matches);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class UnusedSsdpSearch : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedMulticastDnsBrowser : IMulticastDnsBrowser
    {
        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
