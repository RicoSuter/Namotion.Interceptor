using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Wallbox.Model;
using Xunit;

namespace Namotion.Devices.Wallbox.Tests;

public class WallboxChargerPollTests
{
    private static WallboxCharger CreateCharger()
    {
        var httpClientFactory = new TestHttpClientFactory();
        return new WallboxCharger(httpClientFactory, NullLogger<WallboxCharger>.Instance);
    }

    // Connection

    [Theory]
    [InlineData(0)]
    [InlineData(163)]
    public void WhenChargerIsOffline_ThenItIsNotConnectedAndItsPlugStateIsUnknown(int statusId)
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = statusId, IsFinished = false };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.False(charger.IsConnected);
        Assert.Equal("Charger is offline", charger.StatusMessage);
        Assert.Null(charger.IsPluggedIn);
        Assert.Null(charger.IsCharging);
    }

    [Theory]
    [InlineData(161)]  // Ready
    [InlineData(194)]  // Charging
    [InlineData(999)]  // Unknown
    public void WhenChargerIsOnline_ThenItIsConnected(int statusId)
    {
        // Arrange
        var charger = CreateCharger();
        charger.ApplyStatus(new ChargerStatusResponse { StatusId = 0 });

        // Act
        charger.ApplyStatus(new ChargerStatusResponse { StatusId = statusId });

        // Assert
        Assert.True(charger.IsConnected);
        Assert.Null(charger.StatusMessage);
        Assert.NotNull(charger.IsPluggedIn);
    }

    // IsPluggedIn

    [Theory]
    [InlineData(161, false, false)]  // Ready status → not plugged in regardless of Finished
    [InlineData(193, false, true)]   // Charging, Finished=false → plugged in
    [InlineData(193, true, false)]   // Charging, Finished=true → not plugged in (API says finished)
    [InlineData(178, false, true)]   // Paused, Finished=false → plugged in
    [InlineData(210, false, true)]   // LockedCarConnected, Finished=false → plugged in
    public void WhenStatusReceived_ThenIsPluggedInDerivedCorrectly(int statusId, bool finished, bool expectedPluggedIn)
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = statusId, IsFinished = finished };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(expectedPluggedIn, charger.IsPluggedIn);
    }

    // IsCharging

    [Theory]
    [InlineData(193, true)]   // Charging
    [InlineData(194, true)]   // Charging
    [InlineData(195, true)]   // Charging
    [InlineData(196, true)]   // Discharging
    [InlineData(178, false)]  // Paused → not charging
    [InlineData(161, false)]  // Ready → not charging
    [InlineData(180, false)]  // WaitingForCarDemand → not charging
    public void WhenStatusReceived_ThenIsChargingDerivedCorrectly(int statusId, bool expectedCharging)
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = statusId };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(expectedCharging, charger.IsCharging);
    }

    // ChargingCurrent derivation

    [Fact]
    public void WhenChargingCurrentReportedByApi_ThenUsesApiValue()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ChargingCurrent = 16m,
            ChargingPowerInKw = 3.68m,
            CurrentMode = 1
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(16m, charger.ChargingCurrent);
    }

    [Fact]
    public void WhenChargingCurrentZero_ThenDerivesFromPowerAndPhases()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ChargingCurrent = 0,
            ChargingPowerInKw = 7.36m,  // 32A × 230V × 1 phase = 7.36 kW
            CurrentMode = 1              // single phase
        };

        // Act
        charger.ApplyStatus(status);

        // Assert — 7360 / 230 = 32.0A
        Assert.Equal(32.0m, charger.ChargingCurrent);
    }

    [Fact]
    public void WhenChargingCurrentZeroAndThreePhase_ThenDerivesCorrectly()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ChargingCurrent = 0,
            ChargingPowerInKw = 11.04m,  // 16A × 230V × 3 phases = 11.04 kW
            CurrentMode = 3
        };

        // Act
        charger.ApplyStatus(status);

        // Assert — 11040 / (230 × 3) = 16.0A
        Assert.Equal(16.0m, charger.ChargingCurrent);
    }

    [Fact]
    public void WhenChargingCurrentZeroAndNoPower_ThenReturnsZero()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 161,
            ChargingCurrent = 0,
            ChargingPowerInKw = 0,
            CurrentMode = 0
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(0m, charger.ChargingCurrent);
    }

    // ChargingPower conversion

    [Fact]
    public void WhenChargingPowerInKw_ThenConvertsToWatts()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = 193, ChargingPowerInKw = 7.36m };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(7360m, charger.ChargingPower);
    }

    // MaximumChargingPower

    [Fact]
    public void WhenMaxAvailablePowerAndCurrentMode_ThenComputesWatts()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = 193, MaxAvailablePower = 16m, CurrentMode = 3 };

        // Act
        charger.ApplyStatus(status);

        // Assert — 16A × 230V × 3 phases = 11040W
        Assert.Equal(11040m, charger.MaximumChargingPower);
        Assert.Equal(16m, charger.MaximumAvailableChargingCurrent);
    }

    [Fact]
    public void WhenMaxAvailablePowerPositiveButCurrentModeZero_ThenReturnsNull()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = 193, MaxAvailablePower = 16m, CurrentMode = 0 };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Null(charger.MaximumChargingPower);
        Assert.Equal(16m, charger.MaximumAvailableChargingCurrent);
    }

    [Fact]
    public void WhenMaxAvailablePowerZero_ThenReturnsNull()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = 193, MaxAvailablePower = 0m };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Null(charger.MaximumChargingPower);
        Assert.Null(charger.MaximumAvailableChargingCurrent);
    }

    // MaximumChargingCurrent (from config)

    [Fact]
    public void WhenConfigMaxChargingCurrent_ThenSetsValue()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ConfigData = new ChargerConfiguration { MaximumChargingCurrent = 12m }
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(12m, charger.MaximumChargingCurrent);
    }

    // IsLocked

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void WhenLockedValueSet_ThenIsLockedMapsCorrectly(int locked, bool expected)
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ConfigData = new ChargerConfiguration { Locked = locked }
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(expected, charger.IsLocked);
    }

    [Fact]
    public void WhenNoConfigData_ThenIsLockedNull()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = 193 };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Null(charger.IsLocked);
    }

    // Session state

    [Fact]
    public void WhenStateOfChargePresent_ThenChargeLevelSetAsDecimal()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = 193, StateOfCharge = 75 };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(0.75m, charger.Session.ChargeLevel);
        Assert.Equal(0.75m, charger.ChargeLevel); // delegated from Session
    }

    [Fact]
    public void WhenStateOfChargeNull_ThenChargeLevelNull()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = 193, StateOfCharge = null };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Null(charger.Session.ChargeLevel);
    }

    [Fact]
    public void WhenSessionData_ThenConvertsToWattHours()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            AddedEnergy = 5.5m,        // kWh
            AddedGreenEnergy = 2.0m,    // kWh
            AddedGridEnergy = 3.5m,     // kWh
            AddedRange = 30m,
            ChargingTime = 3600,        // seconds
            Cost = 1.50m
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(5500m, charger.Session.AddedEnergy);
        Assert.Equal(2000m, charger.Session.AddedGreenEnergy);
        Assert.Equal(3500m, charger.Session.AddedGridEnergy);
        Assert.Equal(30m, charger.Session.AddedRange);
        Assert.Equal(TimeSpan.FromHours(1), charger.Session.ChargingTime);
        Assert.Equal(1.50m, charger.Session.SessionCost);
    }

    [Fact]
    public void WhenSessionCostZeroAndEnergyAdded_ThenSessionCostNull()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            AddedEnergy = 5.5m,
            Cost = 0m
        };

        // Act
        charger.ApplyStatus(status);

        // Assert — cost not yet calculated by API during active session
        Assert.Null(charger.Session.SessionCost);
    }

    [Fact]
    public void WhenSessionCostZeroAndNoEnergy_ThenSessionCostZero()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 161,
            AddedEnergy = 0m,
            Cost = 0m
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal(0m, charger.Session.SessionCost);
    }

    // EcoSmart

    [Fact]
    public void WhenEcoSmartEnabled_ThenModeSetCorrectly()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ConfigData = new ChargerConfiguration
            {
                Ecosmart = new ChargerEcoSmart { IsEnabled = true, Mode = 1 }
            }
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.True(charger.IsEcoSmartEnabled);
        Assert.Equal(WallboxEcoSmartMode.Eco, charger.EcoSmartMode);
    }

    [Fact]
    public void WhenEcoSmartDisabled_ThenModeIsDisabled()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ConfigData = new ChargerConfiguration
            {
                Ecosmart = new ChargerEcoSmart { IsEnabled = false, Mode = 1 }
            }
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.False(charger.IsEcoSmartEnabled);
        Assert.Equal(WallboxEcoSmartMode.Disabled, charger.EcoSmartMode);
    }

    // Software

    [Fact]
    public void WhenUpdateAvailable_ThenAvailableSoftwareUpdateSet()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ConfigData = new ChargerConfiguration
            {
                Software = new ChargerSoftware
                {
                    CurrentVersion = "5.5.10",
                    IsUpdateAvailable = true,
                    LatestVersion = "5.6.0"
                }
            }
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal("5.5.10", charger.SoftwareVersion);
        Assert.Equal("5.6.0", charger.AvailableSoftwareUpdate);
    }

    [Fact]
    public void WhenNoUpdateAvailable_ThenAvailableSoftwareUpdateNull()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            StatusId = 193,
            ConfigData = new ChargerConfiguration
            {
                Software = new ChargerSoftware
                {
                    CurrentVersion = "5.6.0",
                    IsUpdateAvailable = false,
                    LatestVersion = "5.6.0"
                }
            }
        };

        // Act
        charger.ApplyStatus(status);

        // Assert
        Assert.Equal("5.6.0", charger.SoftwareVersion);
        Assert.Null(charger.AvailableSoftwareUpdate);
    }


    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
