using HueApi.Models;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueLightbulbNormalizationTests
{
    [Fact]
    public void WhenBrightnessIs100_ThenNormalizedTo1()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb("LCT001", isOn: true, brightness: 100.0);

        // Act
        var brightness = lightbulb.Brightness;

        // Assert
        Assert.Equal(1m, brightness);
    }

    [Fact]
    public void WhenBrightnessIs0_ThenNormalizedTo0()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb("LCT001", isOn: true, brightness: 0.0);

        // Act
        var brightness = lightbulb.Brightness;

        // Assert
        Assert.Equal(0m, brightness);
    }

    [Fact]
    public void WhenBrightnessIs50_ThenNormalizedTo05()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb("LCT001", isOn: true, brightness: 50.0);

        // Act
        var brightness = lightbulb.Brightness;

        // Assert
        Assert.Equal(0.5m, brightness);
    }

    [Fact]
    public void WhenMirekIsAtMin_ThenTemperatureIs0()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: true, brightness: 50.0,
            mirek: 153, mirekMin: 153, mirekMax: 500);

        // Act
        var temperature = lightbulb.ColorTemperature;

        // Assert
        Assert.Equal(0m, temperature);
    }

    [Fact]
    public void WhenMirekIsAtMax_ThenTemperatureIs1()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: true, brightness: 50.0,
            mirek: 500, mirekMin: 153, mirekMax: 500);

        // Act
        var temperature = lightbulb.ColorTemperature;

        // Assert
        Assert.Equal(1m, temperature);
    }

    [Fact]
    public void WhenMirekIsMidpoint_ThenTemperatureIs05()
    {
        // Arrange
        // Midpoint between 100 and 300 is 200
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: true, brightness: 50.0,
            mirek: 200, mirekMin: 100, mirekMax: 300);

        // Act
        var temperature = lightbulb.ColorTemperature;

        // Assert
        Assert.Equal(0.5m, temperature);
    }

    [Fact]
    public void WhenNoColorTemperature_ThenNull()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: true, brightness: 50.0,
            mirek: null);

        // Act
        var temperature = lightbulb.ColorTemperature;

        // Assert
        Assert.Null(temperature);
    }

    [Fact]
    public void WhenOnOffLight_ThenBrightnessIsNull()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: true, brightness: 50.0,
            lightType: "On/Off light");

        // Act
        var brightness = lightbulb.Brightness;

        // Assert
        Assert.Null(brightness);
    }

    [Fact]
    public void WhenOnOffLight_ThenIsOnOffLightTrue()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: true,
            lightType: "On/Off light");

        // Act & Assert
        Assert.True(lightbulb.IsOnOffLight);
    }

    [Fact]
    public void WhenDimmableLight_ThenIsOnOffLightFalse()
    {
        // Arrange
        var lightbulb = TestHelpers.CreateLightbulb(
            "LCT001", isOn: true,
            lightType: "Dimmable light");

        // Act & Assert
        Assert.False(lightbulb.IsOnOffLight);
    }
}
