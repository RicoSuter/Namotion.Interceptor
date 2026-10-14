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
}
