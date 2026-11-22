using System.Text.Json;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Tests.Testing;
using Namotion.Interceptor.Testing;

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

    [Fact]
    public void WhenValuesHaveUnexpectedShapes_ThenTheyReadAsMissingAndTheRestOfTheDocumentIsRead()
    {
        // Arrange
        const string json = """
            {
              "response": {
                "vin": "TESTVIN0000000001",
                "charge_state": {
                  "battery_level": "<invalid>",
                  "charge_amps": 6.6,
                  "charge_limit_soc": 3000000000,
                  "charger_voltage": {"nested": [1, 2]},
                  "charger_power": [1, 2],
                  "charger_pilot_current": "",
                  "minutes_to_full_charge": "42",
                  "usable_battery_level": null,
                  "battery_range": "230.45",
                  "charge_rate": "<invalid>",
                  "charge_energy_added": 36.64,
                  "scheduled_charging_start_time": "1795316400",
                  "scheduled_departure_time": 1e30,
                  "charge_enable_request": "true",
                  "charge_port_door_open": "maybe",
                  "fast_charger_present": {"x": 1},
                  "trip_charging": 1,
                  "charging_state": "Complete"
                },
                "drive_state": {
                  "latitude": "47.5",
                  "longitude": true,
                  "heading": 261.4,
                  "power": 1e30,
                  "speed": []
                },
                "vehicle_state": { "car_version": "2026.38.6", "locked": "false" }
              }
            }
            """;

        // Act
        var response = JsonSerializer.Deserialize(json, TeslaJsonContext.Default.TeslaResponseTeslaVehicleDataDto)!;

        // Assert
        var charge = response.Response!.ChargeState!;
        Assert.Null(charge.BatteryLevel);
        Assert.Equal(7, charge.ChargeAmps);
        Assert.Null(charge.ChargeLimitSoc);
        Assert.Null(charge.ChargerVoltage);
        Assert.Null(charge.ChargerPower);
        Assert.Null(charge.ChargerPilotCurrent);
        Assert.Equal(42, charge.MinutesToFullCharge);
        Assert.Null(charge.UsableBatteryLevel);
        Assert.Equal(230.45m, charge.BatteryRange);
        Assert.Null(charge.ChargeRate);
        Assert.Equal(36.64m, charge.ChargeEnergyAdded);
        Assert.Equal(1795316400L, charge.ScheduledChargingStartTime);
        Assert.Null(charge.ScheduledDepartureTime);
        Assert.True(charge.ChargeEnableRequest);
        Assert.Null(charge.ChargePortDoorOpen);
        Assert.Null(charge.FastChargerPresent);
        Assert.Null(charge.TripCharging);
        Assert.Equal("Complete", charge.ChargingState);

        var drive = response.Response.DriveState!;
        Assert.Equal(47.5m, drive.Latitude);
        Assert.Null(drive.Longitude);
        Assert.Equal(261, drive.Heading);
        Assert.Null(drive.Power);
        Assert.Null(drive.Speed);

        Assert.Equal("2026.38.6", response.Response.VehicleState!.CarVersion);
        Assert.False(response.Response.VehicleState.Locked);
    }

    [Fact]
    public void WhenFixturesAreRead_ThenNoApiChangeIsLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var context = TeslaJsonContext.Create(logger);

        // Act
        JsonSerializer.Deserialize(FixtureLoader.Load("vehicle-data.json"), context.TeslaResponseTeslaVehicleDataDto);
        JsonSerializer.Deserialize(FixtureLoader.Load("products.json"), context.TeslaResponseTeslaProductDtoArray);

        // Assert
        Assert.Empty(logger.Warnings);
    }

    // The log-once state is process-wide per target and token type: no other test reads an unexpected value through a logging context.
    [Fact]
    public void WhenValuesHaveUnexpectedTypes_ThenEachIsLoggedOnceAsApiChangeAndPlaceholdersAreNot()
    {
        // Arrange
        var logger = new RecordingLogger();
        var context = TeslaJsonContext.Create(logger);
        const string json = """
            {
              "response": {
                "charge_state": {
                  "battery_level": "unexpected-battery-level",
                  "charge_limit_soc": 3000000000,
                  "charge_port_door_open": {"open": true},
                  "charge_rate": "<invalid>",
                  "charger_pilot_current": "",
                  "minutes_to_full_charge": "42"
                }
              }
            }
            """;

        // Act
        JsonSerializer.Deserialize(json, context.TeslaResponseTeslaVehicleDataDto);
        JsonSerializer.Deserialize(json.Replace("unexpected-battery-level", "changed-battery-level").Replace("3000000000", "3000000001"), context.TeslaResponseTeslaVehicleDataDto);

        // Assert
        Assert.Equal(
        [
            "Tesla API changed? Unexpected int value unexpected-battery-level.",
            "Tesla API changed? Unexpected int value 3000000000.",
            "Tesla API changed? Unexpected bool value StartObject."
        ], logger.Warnings);
    }
}
