using System.Text.Json;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaJsonTests
{
    [Fact]
    public void WhenVehicleDataFixtureIsDeserialized_ThenAllSectionsAreRead()
    {
        // Arrange
        var json = FixtureLoader.Load("vehicle-data.json");

        // Act
        var response = JsonSerializer.Deserialize(json, TeslaJsonContext.Default.TeslaResponseTeslaVehicleDataDto)!;

        // Assert
        var data = response.Response!;
        Assert.Equal("TESTVIN0000000001", data.Vin);
        Assert.Equal("online", data.State);
        Assert.Equal(70, data.ChargeState!.BatteryLevel);
        Assert.Equal("Complete", data.ChargeState.ChargingState);
        Assert.Equal(12.1m, data.ClimateState!.InsideTemp);
        Assert.Equal(47.0m, data.DriveState!.Latitude);
        Assert.Equal("Bar", data.GuiSettings!.GuiTirePressureUnits);
        Assert.Equal("model3", data.VehicleConfig!.CarType);
        Assert.Equal("2026.38.6", data.VehicleState!.CarVersion);
        Assert.Equal(2.9m, data.VehicleState.TpmsPressureFl);
        Assert.Equal("Spotify", data.VehicleState.MediaInfo!.NowPlayingSource);
        Assert.Equal(87.0m, data.VehicleState.SpeedLimitMode!.CurrentLimitMph);
        Assert.Equal(2700, data.VehicleState.SoftwareUpdate!.ExpectedDurationSec);
    }

    [Fact]
    public void WhenProductsFixtureIsDeserialized_ThenVehicleEntriesAreRead()
    {
        // Arrange
        var json = FixtureLoader.Load("products.json");

        // Act
        var response = JsonSerializer.Deserialize(json, TeslaJsonContext.Default.TeslaResponseTeslaProductDtoArray)!;

        // Assert
        var product = Assert.Single(response.Response!);
        Assert.Equal(1000000000000001, product.Id);
        Assert.Equal(2000000000000002, product.VehicleId);
        Assert.Equal("required", product.CommandSigning);
        Assert.Equal("online", product.State);
    }
}
