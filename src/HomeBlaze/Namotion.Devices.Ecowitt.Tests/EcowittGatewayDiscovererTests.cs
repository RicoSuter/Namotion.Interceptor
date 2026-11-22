using System.Net;
using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Ecowitt.Models;
using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittGatewayDiscovererTests
{
    private const string MacAddress = "3C:8A:1F:32:E1:57";
    private const string HostAddress = "192.168.1.216";

    private static EcowittBroadcastReply Reply(string macAddress = MacAddress, string ipAddress = HostAddress, string name = "GW2000A-WIFIE157 V3.3.3") =>
        new(macAddress, IPAddress.Parse(ipAddress), 45000, name);

    private static EcowittDiscoveredGateway Found(string macAddress = MacAddress, string hostAddress = HostAddress) =>
        new(macAddress, hostAddress, "GW2000A-WIFIE157", "V3.3.3");

    private static EcowittGateway CreateGateway(string? hostAddress = null, string? macAddress = null) =>
        new(new TestHttpClientFactory(), NullLogger<EcowittGateway>.Instance)
        {
            HostAddress = hostAddress,
            NetworkInfo = macAddress is null ? null : new EcowittNetworkInfo { MacAddress = macAddress }
        };

    private static async Task<List<DiscoveredSubject>> DiscoverAsync(
        IReadOnlyList<EcowittBroadcastReply> replies, params IInterceptorSubject[] existingSubjects)
    {
        ISubjectDiscoverer discoverer = new EcowittGatewayDiscoverer(cancellationToken => ToAsyncEnumerable(replies, cancellationToken));
        var scan = new DiscoveryScan(new UnusedSsdpSearch(), new UnusedMulticastDnsBrowser(), CancellationToken.None);

        var discovered = new List<DiscoveredSubject>();
        await foreach (var subject in discoverer.DiscoverAsync(scan, existingSubjects, CancellationToken.None))
        {
            discovered.Add(subject);
        }

        return discovered;
    }

    private static async IAsyncEnumerable<EcowittBroadcastReply> ToAsyncEnumerable(
        IReadOnlyList<EcowittBroadcastReply> replies, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var reply in replies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return reply;
        }
    }

    [Fact]
    public async Task WhenAGatewayRepliesToBothRequests_ThenItIsFoundOnce()
    {
        // Arrange
        EcowittBroadcastReply[] replies = [Reply(), Reply(), Reply(macAddress: "3C:8A:1F:00:00:01", ipAddress: "192.168.1.217")];

        // Act
        var discovered = await DiscoverAsync(replies);

        // Assert
        Assert.Equal(2, discovered.Count);
        var subject = discovered[0];
        Assert.Equal(typeof(EcowittGateway), subject.SubjectType);
        Assert.Null(subject.ExistingSubject);
        var result = Assert.IsType<EcowittDiscoveredGateway>(subject.Result);
        Assert.Equal(new EcowittDiscoveredGateway(MacAddress, HostAddress, "GW2000A-WIFIE157", "V3.3.3"), result);
        Assert.Equal(MacAddress, result.Key);
        Assert.Equal("GW2000A-WIFIE157", result.DisplayName);
        Assert.Equal("V3.3.3, 192.168.1.216", result.Detail);
    }

    [Fact]
    public async Task WhenAGatewayIsAlreadyAdded_ThenItIsFoundWithItsSubject()
    {
        // Arrange
        var other = CreateGateway(hostAddress: "192.168.1.10", macAddress: "3C:8A:1F:00:00:02");
        var existing = CreateGateway(hostAddress: "weather.local", macAddress: MacAddress);

        // Act
        var discovered = await DiscoverAsync([Reply()], other, existing);

        // Assert
        Assert.Same(existing, Assert.Single(discovered).ExistingSubject);
    }

    [Theory]
    [InlineData("GW2000A-WIFIE157 V3.3.3", "GW2000A-WIFIE157", "V3.3.3", "GW2000A-WIFIE157", "V3.3.3, 192.168.1.216")]
    [InlineData("GW1100A-WIFI1234", "GW1100A-WIFI1234", null, "GW1100A-WIFI1234", "192.168.1.216")]
    [InlineData("", null, null, "Ecowitt gateway", "192.168.1.216")]
    public void WhenTheReplyIsConverted_ThenTheNameIsSplitIntoDeviceNameAndFirmwareVersion(
        string name, string? deviceName, string? firmwareVersion, string displayName, string detail)
    {
        // Arrange
        var reply = Reply(name: name);

        // Act
        var result = EcowittGatewayDiscoverer.ToResult(reply);

        // Assert
        Assert.Equal(deviceName, result.DeviceName);
        Assert.Equal(firmwareVersion, result.FirmwareVersion);
        Assert.Equal(displayName, result.DisplayName);
        Assert.Equal(detail, result.Detail);
        Assert.Equal(HostAddress, result.HostAddress);
    }

    [Theory]
    [InlineData("3C:8A:1F:32:E1:57")]
    [InlineData("3c:8a:1f:32:e1:57")]
    [InlineData("3C-8A-1F-32-E1-57")]
    [InlineData("3C8A1F32E157")]
    public void WhenTheGatewayReportsTheSameMacAddress_ThenItMatches(string macAddress)
    {
        // Arrange
        var existing = CreateGateway(hostAddress: "192.168.1.10", macAddress: macAddress);

        // Act
        var matches = EcowittGatewayDiscoverer.IsSameGateway(existing, Found());

        // Assert
        Assert.True(matches);
    }

    [Theory]
    [InlineData("192.168.1.216")]
    [InlineData(" 192.168.1.216 ")]
    public void WhenTheGatewayIsConfiguredWithTheSameHostAddress_ThenItMatches(string hostAddress)
    {
        // Arrange
        var existing = CreateGateway(hostAddress: hostAddress);

        // Act
        var matches = EcowittGatewayDiscoverer.IsSameGateway(existing, Found());

        // Assert
        Assert.True(matches);
    }

    [Theory]
    [InlineData("192.168.1.10", "3C:8A:1F:32:E1:58")]
    [InlineData("192.168.1.10", "3C:8A:1F:32:E1")]
    [InlineData("192.168.1.10", "3C:8A:1F:32:E1:57:00")]
    [InlineData("192.168.1.10", null)]
    [InlineData(null, null)]
    [InlineData(null, "")]
    public void WhenNeitherMacAddressNorHostAddressAreTheSame_ThenItDoesNotMatch(string? hostAddress, string? macAddress)
    {
        // Arrange
        var existing = CreateGateway(hostAddress: hostAddress, macAddress: macAddress);

        // Act
        var matches = EcowittGatewayDiscoverer.IsSameGateway(existing, Found());

        // Assert
        Assert.False(matches);
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
