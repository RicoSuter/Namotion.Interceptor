using FluentModbus;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Model;
using Namotion.Devices.Luxtronik.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Luxtronik.Tests;

[Trait("Category", "Integration")]
[Collection(LuxtronikIntegrationCollection.Name)]
public class LuxtronikHeatPumpTests
{
    [Fact]
    public async Task WhenControllerRunsFirmware392_ThenTheModelIsRead()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => source.Diagnostics.IsOperational == true, TimeSpan.FromSeconds(10), message: "The source should be operational.");
            heatPump.UpdateStatus(source.Diagnostics);

            // Assert
            Assert.Equal("3.92.3", heatPump.SoftwareVersion);
            Assert.True(heatPump.OperatingStatus.IsCompressorRunning);
            Assert.Equal(LuxtronikOperatingState.Heating, heatPump.OperatingStatus.OperatingState);
            Assert.Equal(LuxtronikModeStatus.Running, heatPump.Heating.Status);
            Assert.Equal(35.2m, heatPump.Temperatures.Flow.Temperature);
            Assert.Equal(-4.5m, heatPump.Temperatures.Outside.Temperature);
            Assert.Equal(8.1m, heatPump.Temperatures.HeatSourceInlet.Temperature);
            Assert.Equal(48.2m, heatPump.HotWater.Temperature.Temperature);
            Assert.Equal(6500m, heatPump.Energy.ThermalPower);
            Assert.Equal(6500m, heatPump.ThermalPower);
            Assert.Equal(1500m, heatPump.Power);
            Assert.Equal(12345600m, heatPump.EnergyConsumed);
            Assert.Equal(45678900m, heatPump.ThermalEnergyProduced);
            Assert.Equal(12345m, heatPump.OperatingStatus.OperatingHours);
            Assert.Equal(35.0m, heatPump.Heating.SmartHomeControl.Setpoint);
            Assert.Equal(28.0m, heatPump.MixingCircuit1!.HeatingSmartHomeControl.Setpoint);
            Assert.Equal(30000m, heatPump.PowerConsumptionLimit.Limit);
            Assert.True(heatPump.Features.Heating);
            Assert.NotNull(heatPump.Cooling);
            Assert.NotNull(heatPump.MixingCircuit3);
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
            Assert.True(heatPump.IsConnected);
            Assert.Equal(ServiceStatus.Running, heatPump.Status);
            Assert.Null(heatPump.StatusMessage);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenControllerRunsFirmware390_ThenNewerRegistersAreExcludedWithoutFailures()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 90, 1));
        server.Start();
        server.SeedTypicalValues();

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Equal("3.90.1", heatPump.SoftwareVersion);
            Assert.Equal(35.2m, heatPump.Temperatures.Flow.Temperature);
            Assert.Null(heatPump.Temperatures.HeatSourceInlet.Temperature);
            Assert.False(IsClaimed(heatPump.Temperatures.HeatSourceInlet, nameof(LuxtronikTemperatureSensor.Temperature)));
            Assert.Null(heatPump.Energy.TotalThermalEnergy);
            Assert.Null(heatPump.ThermalEnergyProduced);
            Assert.False(IsClaimed(heatPump.OperatingStatus, nameof(LuxtronikOperatingStatus.OperatingHours)));
            Assert.False(IsClaimed(heatPump.Heating.SmartHomeControl, nameof(LuxtronikSmartHomeControl.Level)));
            Assert.False(IsClaimed(heatPump.Heating, nameof(LuxtronikHeating.Locked)));
            Assert.True(IsClaimed(heatPump.Cooling!, nameof(LuxtronikCooling.Locked)));
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenFunctionsAreInactive_ThenTheirSubjectsDoNotExist()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetFeatures(LuxtronikFeature.Heating, LuxtronikFeature.HotWater, LuxtronikFeature.MixingCircuit1Heating);

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Null(heatPump.Cooling);
            Assert.Null(heatPump.Pool);
            Assert.Null(heatPump.Solar);
            Assert.Null(heatPump.RoomControl);
            Assert.Null(heatPump.MixingCircuit2);
            Assert.Null(heatPump.MixingCircuit3);
            var circuit = Assert.IsType<LuxtronikMixingCircuit>(heatPump.MixingCircuit1);
            Assert.True(IsClaimed(circuit.HeatingSmartHomeControl, nameof(LuxtronikSmartHomeControl.Mode)));
            Assert.False(IsClaimed(circuit.CoolingSmartHomeControl, nameof(LuxtronikCoolingSmartHomeControl.Mode)));
            Assert.True(IsClaimed(heatPump.Temperatures.Outside, nameof(LuxtronikTemperatureSensor.Temperature)));
            Assert.Equal(28.0m, circuit.HeatingSmartHomeControl.Setpoint);
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenMixingCircuitOnlyCools_ThenItExistsWithoutItsHeatingRegisters()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetInput<short>(10150, 215);
        server.SetFeatures(LuxtronikFeature.Heating, LuxtronikFeature.MixingCircuit2Cooling);

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            var circuit = Assert.IsType<LuxtronikMixingCircuit>(heatPump.MixingCircuit2);
            Assert.True(IsClaimed(circuit.Temperature, nameof(LuxtronikTemperatureSensor.Temperature)));
            Assert.True(IsClaimed(circuit, nameof(LuxtronikMixingCircuit.Target)));
            Assert.False(IsClaimed(circuit, nameof(LuxtronikMixingCircuit.MinimumTarget)));
            Assert.False(IsClaimed(circuit, nameof(LuxtronikMixingCircuit.MaximumTarget)));
            Assert.True(IsClaimed(circuit.CoolingSmartHomeControl, nameof(LuxtronikCoolingSmartHomeControl.Mode)));
            Assert.False(IsClaimed(circuit.HeatingSmartHomeControl, nameof(LuxtronikSmartHomeControl.Mode)));
            Assert.Null(heatPump.MixingCircuit1);
            Assert.Null(heatPump.MixingCircuit3);
            Assert.Equal(21.5m, circuit.Temperature.Temperature);
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenDiscreteInputsAreRejected_ThenNotAvailableValuesMapToNull()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3), supportsDiscreteInputs: false);
        server.Start();
        server.SeedTypicalValues();
        server.SetInput<ushort>(10007, 0x7FFF);
        server.SetInput(10318, 0x7FFFFFFF);

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            var pool = Assert.IsType<LuxtronikPool>(heatPump.Pool);
            Assert.True(IsClaimed(pool, nameof(LuxtronikPool.Status)));
            Assert.Null(pool.Status);
            Assert.True(IsClaimed(pool, nameof(LuxtronikPool.ElectricalEnergy)));
            Assert.Null(pool.ElectricalEnergy);
            Assert.NotNull(heatPump.Cooling);
            Assert.NotNull(heatPump.MixingCircuit3);
            Assert.False(IsClaimed(heatPump.Features, nameof(LuxtronikFeatures.Heating)));
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenFeatureReadIsBusyOnce_ThenDiscoveryRetriesAndGatesByFeature()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        server.SetFeatures(LuxtronikFeature.Heating, LuxtronikFeature.HotWater);
        server.RejectFeatureReads(1, ModbusExceptionCode.ServerDeviceBusy);

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            var error = Assert.IsType<ModbusResponseException>(source.Diagnostics.LastError);
            Assert.Equal((int)ModbusExceptionCode.ServerDeviceBusy, error.ExceptionCode);
            Assert.True(IsClaimed(heatPump.Features, nameof(LuxtronikFeatures.Cooling)));
            Assert.False(heatPump.Features.Cooling);
            Assert.Null(heatPump.Cooling);
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("3.92.0", false)]
    [InlineData("3.92.1", true)]
    public async Task WhenRoomControlNeedsFirmware3921_ThenItIsClaimedOnlyFromThatVersion(string firmware, bool isClaimed)
    {
        // Arrange
        using var server = new LuxtronikTestServer(Version.Parse(firmware));
        server.Start();
        server.SeedTypicalValues();

        // Act
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Assert
            Assert.Equal(isClaimed, IsClaimed(heatPump.RoomControl!, nameof(LuxtronikRoomControl.TemperatureSetpoint)));
            Assert.Equal(0, source.Diagnostics.Polling.FailedBatches);
            Assert.Equal(0, source.Diagnostics.Polling.UnavailableProperties);
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenElectricalPowerRegisterChanges_ThenDevicePowerFollows()
    {
        // Arrange
        using var server = new LuxtronikTestServer(new Version(3, 92, 3));
        server.Start();
        server.SeedTypicalValues();
        var (heatPump, source, recorder) = await StartAsync(server);
        try
        {
            // Act
            server.SetInput<ushort>(10301, 20);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => heatPump.Power == 2000m, TimeSpan.FromSeconds(10), message: "Power should follow the register.");
        }
        finally
        {
            recorder.Dispose();
            await source.DisposeAsync();
        }
    }

    private static async Task<(LuxtronikHeatPump HeatPump, ModbusSubjectClientSource Source, SourceStateRecorder Recorder)> StartAsync(
        LuxtronikTestServer server)
    {
        var (heatPump, _) = TestHost.CreateAttachedHeatPump();
        var source = heatPump.CreateModbusClientSource(
            new ModbusClientConfiguration
            {
                Host = "127.0.0.1",
                Port = server.Port,
                PollingInterval = TimeSpan.FromMilliseconds(100),
                RetryTime = TimeSpan.FromMilliseconds(200)
            },
            NullLogger.Instance);

        var recorder = SourceStateRecorder.SubscribeTo(source);
        try
        {
            await source.StartAsync(CancellationToken.None);
            await recorder.WaitForStatesAsync(TimeSpan.FromSeconds(30), "The heat pump should synchronize.", SourceState.Synchronized);
            return (heatPump, source, recorder);
        }
        catch
        {
            recorder.Dispose();
            await source.DisposeAsync();
            throw;
        }
    }

    private static bool IsClaimed(IInterceptorSubject subject, string propertyName)
        => new PropertyReference(subject, propertyName).TryGetSource(out _);
}
