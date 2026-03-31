using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeTotalPowerTests
{
    [Fact]
    public void WhenBridgeHasLightsOn_ThenTotalPowerIncludesBridgeAndLights()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.IsConnected = true;
        var light1 = TestHelpers.CreateLightbulb("LWA001", isOn: true, brightness: 100.0); // 9W
        var light2 = TestHelpers.CreateLightbulb("LCT001", isOn: true, brightness: 100.0); // 8.5W
        bridge.Lights = [light1, light2];

        // Act
        var totalPower = bridge.TotalPower;

        // Assert — bridge 3W + 9W + 8.5W = 20.5W
        Assert.Equal(20.5m, totalPower);
    }

    [Fact]
    public void WhenBridgeHasNoLights_ThenTotalPowerIsBridgeOnly()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.IsConnected = true;
        bridge.Lights = [];

        // Act
        var totalPower = bridge.TotalPower;

        // Assert — bridge 3W only
        Assert.Equal(3.0m, totalPower);
    }

    [Fact]
    public void WhenLightPowerIsNull_ThenTotalPowerSkipsIt()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.IsConnected = true;
        var knownLight = TestHelpers.CreateLightbulb("LWA001", isOn: true, brightness: 100.0); // 9W
        var unknownLight = TestHelpers.CreateLightbulb("UNKNOWN", isOn: true, brightness: 100.0); // null power
        bridge.Lights = [knownLight, unknownLight];

        // Act
        var totalPower = bridge.TotalPower;

        // Assert — bridge 3W + 9W = 12W (unknown skipped)
        Assert.Equal(12.0m, totalPower);
    }

    [Fact]
    public void WhenBridgeIsDisconnected_ThenTotalPowerIsNull()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.IsConnected = false;
        bridge.Lights = [TestHelpers.CreateLightbulb("LWA001", isOn: true, brightness: 100.0)];

        // Act
        var totalPower = bridge.TotalPower;

        // Assert
        Assert.Null(totalPower);
    }
}
