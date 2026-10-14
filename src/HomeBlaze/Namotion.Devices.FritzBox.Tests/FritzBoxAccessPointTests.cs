using Namotion.Devices.FritzBox.Parsing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxAccessPointTests
{
    [Fact]
    public void WhenTheAccessPointIsTheMaster_ThenItIsWiredAndTheMeshRoleIsMasterAndValuesAreCopied()
    {
        // Arrange
        var accessPoint = new FritzBoxAccessPoint("AA:AA:AA:00:00:01");
        var info = new AccessPointInfo(
            "AA:AA:AA:00:00:01",
            new HashSet<string> { "AA:AA:AA:00:00:01" },
            "FRITZ!Box 7590",
            "7590",
            "AVM",
            "7.57",
            true,
            true,
            null,
            null);

        // Act
        accessPoint.Update(info, "192.168.178.1");

        // Assert
        Assert.Equal("FRITZ!Box 7590", accessPoint.Name);
        Assert.Equal("7590", accessPoint.Model);
        Assert.Equal("AVM", accessPoint.Manufacturer);
        Assert.Equal("7.57", accessPoint.SoftwareVersion);
        Assert.Equal("192.168.178.1", accessPoint.IpAddress);
        Assert.True(accessPoint.IsConnected);
        Assert.Equal(FritzBoxMeshRole.Master, accessPoint.MeshRole);
        Assert.False(accessPoint.IsWireless);
        Assert.Null(accessPoint.UplinkType);
        Assert.Null(accessPoint.UplinkSpeed);
    }

    [Fact]
    public void WhenASlaveHasAWirelessUplink_ThenItIsWirelessAndTheMeshRoleIsSlave()
    {
        // Arrange
        var accessPoint = new FritzBoxAccessPoint("BB:BB:BB:00:00:02");
        var info = new AccessPointInfo(
            "BB:BB:BB:00:00:02",
            new HashSet<string> { "BB:BB:BB:00:00:02" },
            "FRITZ!Repeater",
            "1200 AX",
            "AVM",
            "7.29",
            false,
            true,
            "WLAN",
            144m);

        // Act
        accessPoint.Update(info, "192.168.178.2");

        // Assert
        Assert.Equal(FritzBoxMeshRole.Slave, accessPoint.MeshRole);
        Assert.True(accessPoint.IsWireless);
        Assert.Equal("WLAN", accessPoint.UplinkType);
        Assert.Equal(144m, accessPoint.UplinkSpeed);
    }

    [Fact]
    public void WhenASlaveHasAWiredUplink_ThenItIsNotWireless()
    {
        // Arrange
        var accessPoint = new FritzBoxAccessPoint("CC:CC:CC:00:00:03");
        var info = new AccessPointInfo(
            "CC:CC:CC:00:00:03",
            new HashSet<string> { "CC:CC:CC:00:00:03" },
            "FRITZ!Repeater",
            "3000",
            "AVM",
            "7.29",
            false,
            true,
            "LAN",
            1000m);

        // Act
        accessPoint.Update(info, "192.168.178.3");

        // Assert
        Assert.False(accessPoint.IsWireless);
    }

    [Fact]
    public void WhenASlaveHasNoUplink_ThenIsWirelessIsUnknown()
    {
        // Arrange
        var accessPoint = new FritzBoxAccessPoint("DD:DD:DD:00:00:04");
        var info = new AccessPointInfo(
            "DD:DD:DD:00:00:04",
            new HashSet<string> { "DD:DD:DD:00:00:04" },
            "FRITZ!Repeater",
            "1200",
            "AVM",
            "7.29",
            false,
            false,
            null,
            null);

        // Act
        accessPoint.Update(info, null);

        // Assert
        Assert.Null(accessPoint.IsWireless);
        Assert.False(accessPoint.IsConnected);
    }
}
