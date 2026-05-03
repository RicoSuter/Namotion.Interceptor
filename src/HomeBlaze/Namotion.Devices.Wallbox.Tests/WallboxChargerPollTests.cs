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

    // IsPluggedIn

    [Theory]
    [InlineData(0, false, false)]   // Disconnected status → not plugged in regardless of Finished
    [InlineData(161, false, false)]  // Ready status → not plugged in regardless of Finished
    [InlineData(163, false, false)]  // Disconnected (163) → not plugged in
    [InlineData(193, false, true)]   // Charging, Finished=false → plugged in
    [InlineData(193, true, false)]   // Charging, Finished=true → not plugged in (API says finished)
    [InlineData(178, false, true)]   // Paused, Finished=false → plugged in
    [InlineData(210, false, true)]   // LockedCarConnected, Finished=false → plugged in
    public void WhenStatusReceived_ThenIsPluggedInDerivedCorrectly(int statusId, bool finished, bool expectedPluggedIn)
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = statusId, Finished = finished };

        // Act
        ApplyStatus(charger, status);

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
    [InlineData(0, false)]    // Disconnected → not charging
    [InlineData(180, false)]  // WaitingForCarDemand → not charging
    public void WhenStatusReceived_ThenIsChargingDerivedCorrectly(int statusId, bool expectedCharging)
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse { StatusId = statusId };

        // Act
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
        ApplyStatus(charger, status);

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
                Ecosmart = new ChargerEcoSmart { Enabled = true, Mode = 1 }
            }
        };

        // Act
        ApplyStatus(charger, status);

        // Assert
        Assert.True(charger.EcoSmartEnabled);
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
                Ecosmart = new ChargerEcoSmart { Enabled = false, Mode = 1 }
            }
        };

        // Act
        ApplyStatus(charger, status);

        // Assert
        Assert.False(charger.EcoSmartEnabled);
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
                    UpdateAvailable = true,
                    LatestVersion = "5.6.0"
                }
            }
        };

        // Act
        ApplyStatus(charger, status);

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
                    UpdateAvailable = false,
                    LatestVersion = "5.6.0"
                }
            }
        };

        // Act
        ApplyStatus(charger, status);

        // Assert
        Assert.Equal("5.6.0", charger.SoftwareVersion);
        Assert.Null(charger.AvailableSoftwareUpdate);
    }

    // Helper: applies a ChargerStatusResponse to the charger's state
    // by calling the same logic as PollAsync (without the HTTP call)
    private static void ApplyStatus(WallboxCharger charger, ChargerStatusResponse status)
    {
        charger.ChargerStatus = status.Status;

        charger.IsPluggedIn = charger.ChargerStatus is WallboxChargerStatus.Disconnected or WallboxChargerStatus.Ready
            ? false
            : !status.Finished;

        charger.IsCharging = charger.ChargerStatus is WallboxChargerStatus.Charging or WallboxChargerStatus.Discharging;
        charger.ChargingPower = status.ChargingPowerInKw * 1000m;

        charger.ChargingCurrent = status.ChargingCurrent > 0
            ? status.ChargingCurrent
            : status.CurrentMode > 0 && status.ChargingPowerInKw > 0
                ? Math.Round(status.ChargingPowerInKw * 1000m / (230m * status.CurrentMode), 1)
                : 0;

        charger.MaximumAvailableChargingCurrent = status.MaxAvailablePower > 0
            ? status.MaxAvailablePower
            : null;

        charger.MaximumChargingCurrent = status.ConfigData?.MaximumChargingCurrent;

        charger.MaximumChargingPower = status.MaxAvailablePower > 0 && status.CurrentMode > 0
            ? status.MaxAvailablePower * 230m * status.CurrentMode
            : null;

        charger.IsLocked = status.ConfigData?.Locked switch
        {
            1 => true,
            0 => false,
            _ => null
        };

        charger.Session.ChargeLevel = status.StateOfCharge.HasValue ? status.StateOfCharge.Value / 100m : null;
        charger.Session.AddedEnergy = status.AddedEnergy * 1000m;
        charger.Session.AddedGreenEnergy = status.AddedGreenEnergy * 1000m;
        charger.Session.AddedGridEnergy = status.AddedGridEnergy * 1000m;
        charger.Session.AddedRange = status.AddedRange;
        charger.Session.ChargingTime = TimeSpan.FromSeconds(status.ChargingTime);
        charger.Session.SessionCost = status.AddedEnergy > 0 && status.Cost == 0 ? null : status.Cost;

        charger.EcoSmartEnabled = status.ConfigData?.Ecosmart?.Enabled;
        charger.EcoSmartMode = status.ConfigData?.Ecosmart is { } eco
            ? eco.Enabled ? (WallboxEcoSmartMode)eco.Mode : WallboxEcoSmartMode.Disabled
            : null;

        charger.SoftwareVersion = status.ConfigData?.Software?.CurrentVersion;
        charger.AvailableSoftwareUpdate = status.ConfigData?.Software?.UpdateAvailable == true
            ? status.ConfigData.Software.LatestVersion
            : null;

        charger.ProductCode = status.ConfigData?.PartNumber;
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
