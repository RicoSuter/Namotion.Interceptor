using Namotion.Devices.FritzBox.Parsing;
using Namotion.Devices.FritzBox.Tests.Testing;
using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxDeviceMatcherTests
{
    [Theory]
    [InlineData("AA:AA:AA:00:00:01")]
    [InlineData("aa-aa-aa-00-00-01")]
    [InlineData("aaaa.aa00.0001")]
    [InlineData("AAAAAA000001")]
    public void WhenADeviceReportsTheMacAddressOfAClientInAnyNotation_ThenItMatchesThatClient(string macAddress)
    {
        // Arrange
        var client = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "laptop");
        var device = new TestNetworkDevice(macAddress);

        // Act
        var matches = FritzBoxDeviceMatcher.Match([device], [client]);

        // Assert
        Assert.Same(device, matches[client]);
    }

    [Fact]
    public void WhenADeviceReportsAMacAddressOfNoClient_ThenItDoesNotMatchByItsIpAddress()
    {
        // Arrange
        var client = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "laptop");
        var device = new TestNetworkDevice("AA:AA:AA:00:00:09", "192.168.178.20", "192.168.178.20");

        // Act
        var matches = FritzBoxDeviceMatcher.Match([device], [client]);

        // Assert
        Assert.Empty(matches);
    }

    [Theory]
    [InlineData(null, "192.168.178.20")]
    [InlineData("192.168.178.20", null)]
    [InlineData(null, "192.168.178.20:80")]
    [InlineData(null, "Laptop")]
    [InlineData(null, "laptop.fritz.box")]
    [InlineData(null, "laptop:8080")]
    public void WhenADeviceReportsNoMacAddress_ThenItMatchesByIpAddressOrHostName(string? ipAddress, string? hostAddress)
    {
        // Arrange
        var client = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "laptop");
        var other = CreateClient("AA:AA:AA:00:00:02", "192.168.178.21", "desktop");
        var device = new TestNetworkDevice(null, ipAddress, hostAddress);

        // Act
        var matches = FritzBoxDeviceMatcher.Match([device], [client, other]);

        // Assert
        Assert.Equal(client, Assert.Single(matches).Key);
    }

    [Fact]
    public void WhenAnAbsentAndAPresentClientHaveTheIpAddress_ThenThePresentOneMatches()
    {
        // Arrange
        var absent = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "old-laptop", isPresent: false);
        var present = CreateClient("AA:AA:AA:00:00:02", "192.168.178.20", "laptop");
        var device = new TestNetworkDevice(hostAddress: "192.168.178.20");

        // Act
        var matches = FritzBoxDeviceMatcher.Match([device], [absent, present]);

        // Assert
        Assert.Equal(present, Assert.Single(matches).Key);
    }

    [Fact]
    public void WhenTwoAbsentClientsHaveTheIpAddress_ThenNeitherMatches()
    {
        // Arrange
        var first = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "laptop", isPresent: false);
        var second = CreateClient("AA:AA:AA:00:00:02", "192.168.178.20", "desktop", isPresent: false);
        var device = new TestNetworkDevice(hostAddress: "192.168.178.20");

        // Act
        var matches = FritzBoxDeviceMatcher.Match([device], [first, second]);

        // Assert
        Assert.Empty(matches);
    }

    [Fact]
    public void WhenTwoDevicesMatchOneClient_ThenTheClientMatchesNeitherInAnyOrder()
    {
        // Arrange
        var client = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "laptop");
        var first = new TestNetworkDevice(hostAddress: "192.168.178.20");
        var second = new TestNetworkDevice(hostAddress: "laptop");
        var third = new TestNetworkDevice(hostAddress: "192.168.178.20");

        // Act
        var forward = FritzBoxDeviceMatcher.Match([first, second, third], [client]);
        var backward = FritzBoxDeviceMatcher.Match([third, second, first], [client]);

        // Assert
        Assert.Empty(forward);
        Assert.Empty(backward);
    }

    [Fact]
    public void WhenOneDeviceMatchesByMacAddressAndAnotherByHost_ThenTheMacAddressMatchWins()
    {
        // Arrange
        var client = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "laptop");
        var byHost = new TestNetworkDevice(hostAddress: "192.168.178.20");
        var byMacAddress = new TestNetworkDevice("AA:AA:AA:00:00:01");

        // Act
        var matches = FritzBoxDeviceMatcher.Match([byHost, byMacAddress], [client]);

        // Assert
        Assert.Same(byMacAddress, matches[client]);
    }

    [Fact]
    public void WhenTheSubjectsAreOfARouter_ThenTheyAreNeverMatched()
    {
        // Arrange
        var client = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "laptop");
        var sameClient = CreateClient("AA:AA:AA:00:00:01", "192.168.178.20", "laptop");

        // Act
        var matches = FritzBoxDeviceMatcher.Match([sameClient, client], [client]);

        // Assert
        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("192.168.178.20", "192.168.178.20")]
    [InlineData(" 192.168.178.20:80 ", "192.168.178.20")]
    [InlineData("[fe80::1]", "fe80::1")]
    [InlineData("[fe80::1]:1400", "fe80::1")]
    [InlineData("FE80:0:0::1", "fe80::1")]
    [InlineData("laptop", null)]
    [InlineData("laptop.fritz.box:80", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void WhenAHostIsNormalizedAsIpAddress_ThenItIsTheCanonicalAddressOrNull(string? host, string? expected)
    {
        // Act
        var ipAddress = FritzBoxDeviceMatcher.NormalizeIpAddress(host);

        // Assert
        Assert.Equal(expected, ipAddress);
    }

    [Theory]
    [InlineData("Laptop", "laptop")]
    [InlineData("laptop.fritz.box", "laptop")]
    [InlineData("laptop:8080", "laptop")]
    [InlineData("192.168.178.20", null)]
    [InlineData("[fe80::1]:1400", null)]
    [InlineData(" ", null)]
    public void WhenTheLabelOfAHostIsTaken_ThenItIsTheFirstLabelInLowerCaseOrNullForAnIpAddress(string? host, string? expected)
    {
        // Act
        var label = FritzBoxDeviceMatcher.GetHostLabel(host);

        // Assert
        Assert.Equal(expected, label);
    }

    private static FritzBoxClient CreateClient(string macAddress, string ipAddress, string hostName, bool isPresent = true)
    {
        var client = new FritzBoxClient(macAddress);
        client.Update(new HostEntry(macAddress, ipAddress, isPresent, hostName, "Ethernet", null, null, false), null);
        return client;
    }
}
