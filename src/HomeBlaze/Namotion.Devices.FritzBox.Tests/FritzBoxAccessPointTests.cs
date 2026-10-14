using Namotion.Devices.FritzBox.Parsing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxAccessPointTests
{
    private const string MacAddress = "AA:AA:AA:00:00:01";

    [Fact]
    public void WhenTheAccessPointIsTheMaster_ThenItIsWiredAndTheMeshRoleIsMasterAndValuesAreCopied()
    {
        // Arrange
        var accessPoint = new FritzBoxAccessPoint(MacAddress);

        // Act
        accessPoint.Update(CreateInfo(isMaster: true), "192.168.178.1");

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

    [Theory]
    [InlineData("WLAN", true)]
    [InlineData("LAN", false)]
    [InlineData(null, null)]
    public void WhenTheAccessPointIsASlave_ThenItsUplinkTypeDecidesWhetherItIsWireless(string? uplinkType, bool? expectedIsWireless)
    {
        // Arrange
        var accessPoint = new FritzBoxAccessPoint(MacAddress);
        decimal? uplinkSpeed = uplinkType is null ? null : 144m;

        // Act
        accessPoint.Update(CreateInfo(isMaster: false, uplinkType, uplinkSpeed), null);

        // Assert
        Assert.Equal(FritzBoxMeshRole.Slave, accessPoint.MeshRole);
        Assert.Equal(expectedIsWireless, accessPoint.IsWireless);
        Assert.Equal(uplinkType, accessPoint.UplinkType);
        Assert.Equal(uplinkSpeed, accessPoint.UplinkSpeed);
    }

    private static AccessPointInfo CreateInfo(bool isMaster, string? uplinkType = null, decimal? uplinkSpeed = null) =>
        new(MacAddress, new HashSet<string> { MacAddress }, "FRITZ!Box 7590", "7590", "AVM", "7.57", isMaster, true, uplinkType, uplinkSpeed);
}
