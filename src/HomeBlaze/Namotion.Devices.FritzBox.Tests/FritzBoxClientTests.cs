using Namotion.Devices.FritzBox.Parsing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxClientTests
{
    [Fact]
    public void WhenAPresentClientIsUpdated_ThenItTakesItsHostValuesAndPosition()
    {
        // Arrange
        var client = new FritzBoxClient("AA:AA:AA:00:00:01");
        var host = new HostEntry("AA:AA:AA:00:00:01", " 192.168.178.20 ", true, "laptop", "802.11", 0, 144, false);
        var position = new ClientPosition("BB:BB:BB:00:00:02", "FRITZ!Repeater", "2.4GHz", null, 144m, true);

        // Act
        client.Update(host, position);

        // Assert
        Assert.Equal("laptop", client.Title);
        Assert.True(client.IsPresent);
        Assert.Equal("192.168.178.20", client.IpAddress);
        Assert.Equal(FritzBoxInterfaceType.Wlan, client.InterfaceType);
        Assert.True(client.IsWireless);
        Assert.Equal("FRITZ!Repeater", client.AccessPointName);
        Assert.Equal("BB:BB:BB:00:00:02", client.AccessPointMacAddress);
        Assert.Equal("2.4GHz", client.Band);
        Assert.Equal(144m, client.LinkSpeed);
    }

    [Fact]
    public void WhenAClientBecomesAbsent_ThenItsPositionIsCleared()
    {
        // Arrange
        var client = new FritzBoxClient("AA:AA:AA:00:00:01");
        client.Update(
            new HostEntry("AA:AA:AA:00:00:01", "192.168.178.20", true, "laptop", "802.11", 0, 144, false),
            new ClientPosition("BB:BB:BB:00:00:02", "FRITZ!Repeater", "2.4GHz", null, 144m, true));

        // Act
        client.Update(new HostEntry("AA:AA:AA:00:00:01", "192.168.178.20", false, "laptop", "802.11", 0, 0, false), null);

        // Assert
        Assert.False(client.IsPresent);
        Assert.Null(client.AccessPointName);
        Assert.Null(client.AccessPointMacAddress);
        Assert.Null(client.Band);
        Assert.Null(client.Port);
        Assert.Null(client.LinkSpeed);
        Assert.True(client.IsWireless);
    }

    [Theory]
    [InlineData("8A:F5:83:94:1E:19", true)]
    [InlineData("00:04:4B:A9:B7:3C", false)]
    public void WhenAClientIsCreated_ThenItTellsWhetherItsMacAddressIsPrivate(string macAddress, bool expected)
    {
        // Act
        var client = new FritzBoxClient(macAddress);

        // Assert
        Assert.Equal(expected, client.IsPrivateMacAddress);
    }

    [Fact]
    public void WhenAClientDeparts_ThenItIsAbsentWithoutPositionAndKeepsItsHostNameAndAddress()
    {
        // Arrange
        var client = new FritzBoxClient("AA:AA:AA:00:00:01");
        client.Update(
            new HostEntry("AA:AA:AA:00:00:01", "192.168.178.20", true, "laptop", "Ethernet", 2, 1000, false),
            new ClientPosition("BB:BB:BB:00:00:01", "fritz.box", null, 2, 1000m, false));

        // Act
        client.Depart();

        // Assert
        Assert.True(client.IsDeparted);
        Assert.False(client.IsPresent);
        Assert.Null(client.AccessPointName);
        Assert.Null(client.AccessPointMacAddress);
        Assert.Null(client.Port);
        Assert.Null(client.LinkSpeed);
        Assert.Equal("laptop", client.HostName);
        Assert.Equal("192.168.178.20", client.IpAddress);
    }

    [Fact]
    public void WhenADepartedClientIsUpdated_ThenItIsNoLongerDeparted()
    {
        // Arrange
        var client = new FritzBoxClient("AA:AA:AA:00:00:01");
        client.Depart();

        // Act
        client.Update(new HostEntry("AA:AA:AA:00:00:01", "192.168.178.20", true, "laptop", "Ethernet", 2, 1000, false), null);

        // Assert
        Assert.False(client.IsDeparted);
        Assert.True(client.IsPresent);
    }

    [Theory]
    [InlineData("Ethernet", FritzBoxInterfaceType.Ethernet)]
    [InlineData("802.11", FritzBoxInterfaceType.Wlan)]
    [InlineData("HomePlug", FritzBoxInterfaceType.HomePlug)]
    [InlineData("", FritzBoxInterfaceType.Unknown)]
    [InlineData(null, FritzBoxInterfaceType.Unknown)]
    public void WhenAnInterfaceTypeIsRead_ThenItMapsToTheEnum(string? value, FritzBoxInterfaceType expected)
    {
        // Act
        var interfaceType = FritzBoxInterfaceTypes.Parse(value);

        // Assert
        Assert.Equal(expected, interfaceType);
    }

    [Theory]
    [InlineData("Ethernet", false)]
    [InlineData("802.11", true)]
    [InlineData("HomePlug", false)]
    [InlineData("", null)]
    public void WhenAClientHasNoMeshPosition_ThenIsWirelessComesFromTheInterfaceType(string interfaceTypeValue, bool? expected)
    {
        // Arrange
        var client = new FritzBoxClient("AA:AA:AA:00:00:01");
        var host = new HostEntry("AA:AA:AA:00:00:01", "192.168.178.20", true, "laptop", interfaceTypeValue, 1, 100, false);

        // Act
        client.Update(host, null);

        // Assert
        Assert.Equal(expected, client.IsWireless);
    }

    [Fact]
    public void WhenAnActiveClientHasNoMeshPosition_ThenLinkSpeedAndPortComeFromTheHost()
    {
        // Arrange
        var client = new FritzBoxClient("AA:AA:AA:00:00:01");
        var host = new HostEntry("AA:AA:AA:00:00:01", "192.168.178.20", true, "laptop", "Ethernet", 3, 1000, false);

        // Act
        client.Update(host, null);

        // Assert
        Assert.Equal(1000, client.LinkSpeed);
        Assert.Equal(3, client.Port);
    }

    [Fact]
    public void WhenAnInactiveClientHasAPositiveHostSpeed_ThenLinkSpeedAndPortAreNull()
    {
        // Arrange
        var client = new FritzBoxClient("AA:AA:AA:00:00:01");
        var host = new HostEntry("AA:AA:AA:00:00:01", "192.168.178.20", false, "laptop", "Ethernet", 3, 1000, false);

        // Act
        client.Update(host, null);

        // Assert
        Assert.Null(client.LinkSpeed);
        Assert.Null(client.Port);
    }
}
