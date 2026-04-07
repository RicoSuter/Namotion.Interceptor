using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Wallbox.Model;
using Xunit;

namespace Namotion.Devices.Wallbox.Tests;

public class WallboxChargerDerivedPropertyTests
{
    private static WallboxCharger CreateCharger()
    {
        var httpClientFactory = new TestHttpClientFactory();
        return new WallboxCharger(httpClientFactory, NullLogger<WallboxCharger>.Instance);
    }

    // Title

    [Fact]
    public void WhenNameSet_ThenTitleIsName()
    {
        // Arrange
        var charger = CreateCharger();

        // Act
        charger.Name = "Garage Charger";

        // Assert
        Assert.Equal("Garage Charger", charger.Title);
    }

    [Fact]
    public void WhenNameEmpty_ThenTitleIsSerialNumber()
    {
        // Arrange
        var charger = CreateCharger();

        // Act
        charger.SerialNumber = "910037";

        // Assert
        Assert.Equal("910037", charger.Title);
    }

    // IconColor

    [Fact]
    public void WhenDisconnected_ThenIconColorError()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = false;

        // Act & Assert
        Assert.Equal("Error", charger.IconColor);
    }

    [Fact]
    public void WhenConnectedAndCharging_ThenIconColorSuccess()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = true;
        charger.IsCharging = true;

        // Act & Assert
        Assert.Equal("Success", charger.IconColor);
    }

    [Fact]
    public void WhenConnectedAndPluggedIn_ThenIconColorWarning()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = true;
        charger.IsCharging = false;
        charger.IsPluggedIn = true;

        // Act & Assert
        Assert.Equal("Warning", charger.IconColor);
    }

    [Fact]
    public void WhenConnectedAndIdle_ThenIconColorDefault()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = true;
        charger.IsCharging = false;
        charger.IsPluggedIn = false;

        // Act & Assert
        Assert.Equal("Default", charger.IconColor);
    }

    // Operation enable conditions

    [Fact]
    public void WhenConnectedAndNotLocked_ThenLockEnabled()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = true;
        charger.IsLocked = false;

        // Act & Assert
        Assert.True(charger.LockAsync_IsEnabled);
        Assert.False(charger.UnlockAsync_IsEnabled);
    }

    [Fact]
    public void WhenConnectedAndLocked_ThenUnlockEnabled()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = true;
        charger.IsLocked = true;

        // Act & Assert
        Assert.False(charger.LockAsync_IsEnabled);
        Assert.True(charger.UnlockAsync_IsEnabled);
    }

    [Fact]
    public void WhenDisconnected_ThenNoOperationsEnabled()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = false;

        // Act & Assert
        Assert.False(charger.LockAsync_IsEnabled);
        Assert.False(charger.UnlockAsync_IsEnabled);
        Assert.False(charger.PauseChargingAsync_IsEnabled);
        Assert.False(charger.ResumeChargingAsync_IsEnabled);
    }

    [Fact]
    public void WhenCharging_ThenPauseEnabled()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = true;
        charger.IsCharging = true;

        // Act & Assert
        Assert.True(charger.PauseChargingAsync_IsEnabled);
    }

    [Fact]
    public void WhenPaused_ThenResumeEnabled()
    {
        // Arrange
        var charger = CreateCharger();
        charger.IsConnected = true;
        charger.ChargerStatus = WallboxChargerStatus.Paused;

        // Act & Assert
        Assert.True(charger.ResumeChargingAsync_IsEnabled);
    }

    // Manufacturer is always "Wallbox"

    [Fact]
    public void WhenCreated_ThenManufacturerIsWallbox()
    {
        // Arrange & Act
        var charger = CreateCharger();

        // Assert
        Assert.Equal("Wallbox", charger.Manufacturer);
    }

    // IPowerSensor delegation

    [Fact]
    public void WhenChargingPowerSet_ThenPowerDelegates()
    {
        // Arrange
        var charger = CreateCharger();

        // Act
        charger.ChargingPower = 7360m;

        // Assert
        Assert.Equal(7360m, charger.Power);
    }

    [Fact]
    public void WhenTotalEnergySet_ThenEnergyConsumedDelegates()
    {
        // Arrange
        var charger = CreateCharger();

        // Act
        charger.TotalEnergyConsumed = 50000m;

        // Assert
        Assert.Equal(50000m, charger.EnergyConsumed);
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
