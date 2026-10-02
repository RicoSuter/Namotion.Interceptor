using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Registry;

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
    [InlineData(3600000, 3600000)]
    [InlineData(7200000, 3600000)]
    public void WhenPollingIntervalIsConfigured_ThenTheEffectiveIntervalIsClampedToItsLimits(int configuredMilliseconds, int expectedMilliseconds)
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();

        // Act
        heatPump.PollingInterval = TimeSpan.FromMilliseconds(configuredMilliseconds);

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), heatPump.GetEffectivePollingInterval());
    }

    [Fact]
    public void WhenSourceHasNotConnectedYet_ThenStatusIsStartingAndLastUpdatedIsKept()
    {
        // Arrange
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        var lastUpdated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        heatPump.LastUpdated = lastUpdated;
        using var source = heatPump.CreateModbusClientSource(new ModbusClientConfiguration { Host = "127.0.0.1" }, NullLogger.Instance);

        // Act
        heatPump.UpdateStatus(source.Diagnostics);

        // Assert
        Assert.False(heatPump.IsConnected);
        Assert.Equal(ServiceStatus.Starting, heatPump.Status);
        Assert.Equal("Connecting...", heatPump.StatusMessage);
        Assert.Equal(lastUpdated, heatPump.LastUpdated);
    }

    [Fact]
    public async Task WhenAddedWithoutAContextResolver_ThenTheHeatPumpIsRegisteredInItsOwnContext()
    {
        // Arrange
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddLuxtronikHeatPump();
        await using var provider = services.BuildServiceProvider();
        var activation = Assert.Single(provider.GetServices<IHostedService>());

        // Act
        await activation.StartAsync(CancellationToken.None);

        try
        {
            // Assert
            var heatPump = provider.GetRequiredService<LuxtronikHeatPump>();
            Assert.NotNull(heatPump.TryGetRegisteredSubject());
        }
        finally
        {
            await activation.StopAsync(CancellationToken.None);
        }
    }
}
