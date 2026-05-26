using HueApi.Models;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

public class HueGroupAggregationTests
{
    [Fact]
    public void WhenGroupHasLights_ThenLumenIsSummed()
    {
        // Arrange
        // LCT001 on => 595 lumen, LWA001 on => 806 lumen
        var light1 = TestHelpers.CreateLightbulb("LCT001", isOn: true, brightness: 100.0);
        var light2 = TestHelpers.CreateLightbulb("LWA001", isOn: true, brightness: 100.0);

        var room = TestHelpers.CreateRoom("Living Room");
        var groupedLight = TestHelpers.CreateGroupedLight(isOn: true, brightness: 75.0);

        var group = new HueGroup(room, groupedLight, [light1, light2], null!);

        // Act
        var lumen = group.Lumen;

        // Assert
        Assert.Equal(595m + 806m, lumen);
    }

    [Fact]
    public void WhenGroupHasLightsWithSomeLumenNull_ThenSumSkipsNulls()
    {
        // Arrange
        // LCT001 on => 595 lumen, LTW001 on => null lumen
        var light1 = TestHelpers.CreateLightbulb("LCT001", isOn: true, brightness: 100.0);
        var light2 = TestHelpers.CreateLightbulb("LTW001", isOn: true, brightness: 100.0); // LTW001 lumen is null

        var room = TestHelpers.CreateRoom();
        var groupedLight = TestHelpers.CreateGroupedLight(isOn: true);

        var group = new HueGroup(room, groupedLight, [light1, light2], null!);

        // Act
        var lumen = group.Lumen;

        // Assert - LINQ Sum on decimal? skips null values, so 595 + null = 595
        Assert.Equal(595m, lumen);
    }

    [Fact]
    public void WhenGroupIsOnFromGroupedLight_ThenIsOnTrue()
    {
        // Arrange
        var room = TestHelpers.CreateRoom();
        var groupedLight = TestHelpers.CreateGroupedLight(isOn: true, brightness: 50.0);
        var group = new HueGroup(room, groupedLight, [], null!);

        // Act & Assert
        Assert.True(group.IsOn);
    }

    [Fact]
    public void WhenGroupIsOffFromGroupedLight_ThenIsOnFalse()
    {
        // Arrange
        var room = TestHelpers.CreateRoom();
        var groupedLight = TestHelpers.CreateGroupedLight(isOn: false);
        var group = new HueGroup(room, groupedLight, [], null!);

        // Act & Assert
        Assert.False(group.IsOn);
    }

    [Fact]
    public void WhenGroupedLightHasBrightness_ThenBrightnessNormalized()
    {
        // Arrange
        var room = TestHelpers.CreateRoom();
        var groupedLight = TestHelpers.CreateGroupedLight(isOn: true, brightness: 75.0);
        var group = new HueGroup(room, groupedLight, [], null!);

        // Act
        var brightness = group.Brightness;

        // Assert
        Assert.Equal(0.75m, brightness);
    }

    [Fact]
    public void WhenGroupHasNoGroupedLight_ThenIsOnNull()
    {
        // Arrange
        var room = TestHelpers.CreateRoom();
        var group = new HueGroup(room, null, [], null!);

        // Act & Assert
        Assert.Null(group.IsOn);
    }

    [Fact]
    public void WhenGroupHasTitle_ThenTitleMatchesRoomName()
    {
        // Arrange
        var room = TestHelpers.CreateRoom("Bedroom");
        var group = new HueGroup(room, null, [], null!);

        // Act & Assert
        Assert.Equal("Bedroom", group.Title);
    }
}
