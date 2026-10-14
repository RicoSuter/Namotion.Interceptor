using Namotion.Devices.FritzBox.Model;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxWlanTests
{
    [Fact]
    public void WhenTheSwitchStateIsNotCurrent_ThenIsOnIsKeptButOtherValuesUpdate()
    {
        // Arrange
        var wlan = new FritzBoxWlan(FritzBoxWlanRoles.Band24, "urn:dslforum-org:service:WLANConfiguration:1");
        wlan.Update(
            new WlanInfo(FritzBoxWlanRoles.Band24, "urn:dslforum-org:service:WLANConfiguration:1", true, "MyWifi", "2.4GHz", 6, 3),
            isSwitchStateCurrent: true);

        // Act
        wlan.Update(
            new WlanInfo(FritzBoxWlanRoles.Band24, "urn:dslforum-org:service:WLANConfiguration:1", false, "MyWifi2", "2.4GHz", 11, 5),
            isSwitchStateCurrent: false);

        // Assert
        Assert.True(wlan.IsOn);
        Assert.Equal("MyWifi2", wlan.Ssid);
        Assert.Equal("2.4GHz", wlan.Band);
        Assert.Equal(11, wlan.Channel);
        Assert.Equal(5, wlan.ClientCount);
    }

    [Fact]
    public void WhenTheSwitchStateIsCurrent_ThenIsOnIsWritten()
    {
        // Arrange
        var wlan = new FritzBoxWlan(FritzBoxWlanRoles.Band5, "urn:dslforum-org:service:WLANConfiguration:2");

        // Act
        wlan.Update(
            new WlanInfo(FritzBoxWlanRoles.Band5, "urn:dslforum-org:service:WLANConfiguration:2", false, "MyWifi", "5GHz", 36, 2),
            isSwitchStateCurrent: true);

        // Assert
        Assert.False(wlan.IsOn);
        Assert.Equal("MyWifi", wlan.Ssid);
        Assert.Equal("5GHz", wlan.Band);
        Assert.Equal(36, wlan.Channel);
        Assert.Equal(2, wlan.ClientCount);
    }
}
