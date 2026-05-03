using HueApi.Models;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueLightbulbPowerTests
{
    [Theory]
    [InlineData("LCT001", 8.5)]
    [InlineData("LST002", 20)]
    [InlineData("LWA001", 9)]
    [InlineData("LCA008", 13.5)]
    [InlineData("LCT015", 9.5)]
    [InlineData("LCT016", 6.5)]
    [InlineData("LCL001", 25)]
    [InlineData("LTG002", 5)]
    public void WhenModelIsKnownAndOn_ThenPowerMatchesTable(string modelId, double expectedPower)
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(modelId, isOn: true, brightness: 100.0);

        // Act
        var power = lightbulb.Power;

        // Assert
        Assert.Equal((decimal)expectedPower, power);
    }

    [Fact]
    public void WhenLightIsOffAndConnected_ThenPowerIs0Point5W()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: false, brightness: 0.0,
            connectivityStatus: ConnectivityStatus.connected);

        // Act
        var power = lightbulb.Power;

        // Assert
        Assert.Equal(0.5m, power);
    }

    [Fact]
    public void WhenModelUnknownAndOn_ThenPowerIsNull()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "UNKNOWN_MODEL", isOn: true, brightness: 100.0);

        // Act
        var power = lightbulb.Power;

        // Assert
        Assert.Null(power);
    }

    [Fact]
    public void WhenLightIsOffAndDisconnected_ThenPowerIsNull()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: false, brightness: 0.0,
            connectivityStatus: ConnectivityStatus.connectivity_issue);

        // Act
        var power = lightbulb.Power;

        // Assert
        Assert.Null(power);
    }

    [Theory]
    [InlineData("LCT001", 595)]
    [InlineData("LST002", 1600)]
    [InlineData("LWA001", 806)]
    [InlineData("LCA008", 1600)]
    public void WhenModelIsKnownAndOn_ThenLumenMatchesTable(string modelId, double expectedLumen)
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(modelId, isOn: true, brightness: 100.0);

        // Act
        var lumen = lightbulb.Lumen;

        // Assert
        Assert.Equal((decimal)expectedLumen, lumen);
    }

    [Fact]
    public void WhenLightIsOffAndConnected_ThenLumenIsZero()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: false, brightness: 0.0,
            connectivityStatus: ConnectivityStatus.connected);

        // Act
        var lumen = lightbulb.Lumen;

        // Assert
        Assert.Equal(0m, lumen);
    }

    [Fact]
    public void WhenLightIsOffAndDisconnected_ThenLumenIsNull()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: false, brightness: 0.0,
            connectivityStatus: ConnectivityStatus.connectivity_issue);

        // Act
        var lumen = lightbulb.Lumen;

        // Assert
        Assert.Null(lumen);
    }
}
