using Namotion.Devices.FritzBox.Model;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxWlanTests
{
    [Fact]
    public void WhenUpdated_ThenEveryValueButTheSwitchStateIsWritten()
    {
        // Arrange
        var wlan = new FritzBoxWlan(FritzBoxWlanRoles.Band24, "urn:dslforum-org:service:WLANConfiguration:1");
        wlan.IsOn = true;

        // Act
        wlan.Update(new WlanInfo(FritzBoxWlanRoles.Band24, "urn:dslforum-org:service:WLANConfiguration:2", false, "MyWifi2", "2.4GHz", 11, 5));

        // Assert
        Assert.True(wlan.IsOn);
        Assert.Equal("urn:dslforum-org:service:WLANConfiguration:2", wlan.ServiceType);
        Assert.Equal("MyWifi2", wlan.Ssid);
        Assert.Equal("2.4GHz", wlan.Band);
        Assert.Equal(11, wlan.Channel);
        Assert.Equal(5, wlan.ClientCount);
    }
}
