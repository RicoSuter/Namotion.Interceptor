using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueBridgeCombinedPowerTests
{
    [Fact]
    public void WhenBridgeHasLightsOn_ThenCombinedPowerIncludesBridgeAndLights()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.IsConnected = true;
        var light1 = TestHelpers.CreateLightbulb("LWA001", isOn: true, brightness: 100.0); // 9W
        var light2 = TestHelpers.CreateLightbulb("LCT001", isOn: true, brightness: 100.0); // 8.5W
        bridge.Lights = new Dictionary<string, HueLightbulb>
        {
            [Guid.NewGuid().ToString()] = light1,
            [Guid.NewGuid().ToString()] = light2,
        };

        // Act
        var combinedPower = bridge.CombinedPower;

        // Assert
        // Bridge 3 W + 9 W + 8.5 W = 20.5 W
        Assert.Equal(20.5m, combinedPower);
    }

    [Fact]
    public void WhenBridgeHasNoLights_ThenCombinedPowerIsBridgeOnly()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.IsConnected = true;
        bridge.Lights = new();

        // Act
        var combinedPower = bridge.CombinedPower;

        // Assert
        // Bridge 3 W only
        Assert.Equal(3.0m, combinedPower);
    }

    [Fact]
    public void WhenLightPowerIsNull_ThenCombinedPowerSkipsIt()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.IsConnected = true;
        var knownLight = TestHelpers.CreateLightbulb("LWA001", isOn: true, brightness: 100.0); // 9W
        var unknownLight = TestHelpers.CreateLightbulb("UNKNOWN", isOn: true, brightness: 100.0); // null power
        bridge.Lights = new Dictionary<string, HueLightbulb>
        {
            [Guid.NewGuid().ToString()] = knownLight,
            [Guid.NewGuid().ToString()] = unknownLight,
        };

        // Act
        var combinedPower = bridge.CombinedPower;

        // Assert
        // Bridge 3 W + 9 W = 12 W, the unknown light is skipped
        Assert.Equal(12.0m, combinedPower);
    }

    [Fact]
    public void WhenBridgeIsDisconnected_ThenCombinedPowerIsNull()
    {
        // Arrange
        var bridge = TestHelpers.CreateTestBridge();
        bridge.IsConnected = false;
        bridge.Lights = new Dictionary<string, HueLightbulb>
        {
            [Guid.NewGuid().ToString()] = TestHelpers.CreateLightbulb("LWA001", isOn: true, brightness: 100.0),
        };

        // Act
        var combinedPower = bridge.CombinedPower;

        // Assert
        Assert.Null(combinedPower);
    }
}
