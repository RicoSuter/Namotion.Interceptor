using System.Reactive.Concurrency;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Devices.Luxtronik.Tests;

public class LuxtronikHeatPumpDeviceTests
{
    [Fact]
    public void WhenElectricalValuesAreSet_ThenPowerSensorReportsThem()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.Energy.ElectricalPower = 1500m;
        heatPump.Energy.TotalElectricalEnergy = 12345600m;

        // Assert
        IPowerSensor powerSensor = heatPump;
        Assert.Equal(1500m, powerSensor.Power);
        Assert.Equal(12345600m, powerSensor.EnergyConsumed);
    }

    [Fact]
    public void WhenThermalValuesAreSet_ThenThermalPowerSensorReportsThem()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.Energy.ThermalPower = 6500m;
        heatPump.Energy.TotalThermalEnergy = 45678900m;

        // Assert
        IThermalPowerSensor thermalPowerSensor = heatPump;
        Assert.Equal(6500m, thermalPowerSensor.ThermalPower);
        Assert.Equal(45678900m, thermalPowerSensor.ThermalEnergyProduced);
    }

    [Fact]
    public void WhenChildValueChanges_ThenDerivedPowerChangeIsPublished()
    {
        // Arrange
        var (heatPump, context) = TestHost.CreateAttachedHeatPump();
        var changedProperties = new List<string>();
        using var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changedProperties.Add(change.Property.Name));

        // Act
        heatPump.Energy.ElectricalPower = 2000m;

        // Assert
        Assert.Contains(nameof(LuxtronikHeatPump.Power), changedProperties);
    }

    [Fact]
    public void WhenNameIsSet_ThenNameIsTheTitle()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.Name = "Basement heat pump";

        // Assert
        Assert.Equal("Basement heat pump", heatPump.Title);
    }

    [Fact]
    public void WhenNameIsCleared_ThenDefaultTitleIsUsed()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        heatPump.Name = "Basement heat pump";

        // Act
        heatPump.Name = "";

        // Assert
        Assert.Equal("Luxtronik Heat Pump", heatPump.Title);
    }

    [Fact]
    public void WhenConstructed_ThenDefaultsAreSet()
    {
        // Act
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Assert
        Assert.Equal(502, heatPump.Port);
        Assert.Equal(TimeSpan.FromSeconds(30), heatPump.PollingInterval);
        Assert.Equal(10000, heatPump.Heating.SmartHomeControl.BaseAddress);
        Assert.Equal(10005, heatPump.HotWater.SmartHomeControl.BaseAddress);
        Assert.Null(heatPump.Cooling);
        Assert.Null(heatPump.MixingCircuit1);
    }

    [Theory]
    [InlineData(0, 10000)]
    [InlineData(2000, 10000)]
    [InlineData(10000, 10000)]
    [InlineData(30000, 30000)]
    public void WhenPollingIntervalIsConfigured_ThenTheSourcePollsNoFasterThanTheMinimum(int configuredMilliseconds, int expectedMilliseconds)
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.PollingInterval = TimeSpan.FromMilliseconds(configuredMilliseconds);

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), heatPump.GetEffectivePollingInterval());
    }
}
