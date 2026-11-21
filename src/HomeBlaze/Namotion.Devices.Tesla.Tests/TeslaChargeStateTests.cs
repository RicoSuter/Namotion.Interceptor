using CarServer;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaChargeStateTests
{
    private static TeslaChargeState CreateFromFixture()
    {
        var data = JsonSerializer.Deserialize(FixtureLoader.Load("vehicle-data.json"), TeslaJsonContext.Default.TeslaResponseTeslaVehicleDataDto)!.Response!;
        var charge = new TeslaChargeState(NullLogger.Instance);
        charge.Update(data.ChargeState!);
        return charge;
    }

    [Fact]
    public void WhenFixtureIsApplied_ThenValuesAreConverted()
    {
        // Act
        var charge = CreateFromFixture();

        // Assert
        Assert.Equal(0.7m, charge.BatteryLevel);
        Assert.Equal(0.69m, charge.UsableBatteryLevel);
        Assert.Equal(0.7m, charge.ChargeLimit);
        Assert.Equal(0.5m, charge.MinimumChargeLimit);
        Assert.Equal(1m, charge.MaximumChargeLimit);
        Assert.Equal(0.8m, charge.StandardChargeLimit);
        Assert.Equal(TeslaChargingState.Complete, charge.ChargingState);
        Assert.Equal(36640m, charge.AddedEnergy);
        Assert.Equal(Math.Round(230.45m * 1.609344m, 3), Math.Round(charge.Range!.Value, 3));
        Assert.True(charge.IsChargePortDoorOpen);
        Assert.Equal(TeslaChargePortLatch.Engaged, charge.ChargePortLatch);
        Assert.Equal(TeslaScheduledChargingMode.StartAt, charge.ScheduledChargingMode);
        Assert.Equal(TimeSpan.FromHours(4), charge.ScheduledChargingStartTime);
        Assert.Equal("IEC", charge.ChargeCable);
    }

    [Fact]
    public void WhenNotCharging_ThenPlaceholderReadingsAreNull()
    {
        // Act
        var charge = CreateFromFixture();

        // Assert
        Assert.Null(charge.ChargerVoltage);        // fixture reports 1 V while not charging
        Assert.Null(charge.FastChargerBrand);      // "<invalid>"
        Assert.Null(charge.FastChargerType);
        Assert.Null(charge.RemainingChargingTime); // not charging
        Assert.Equal(0m, charge.ChargingPower);
        Assert.True(charge.IsPluggedIn);           // charging state Complete means the cable is plugged in
        Assert.False(charge.IsCharging);
    }

    [Fact]
    public void WhenCachedSnapshotIsNewer_ThenItUpdatesAndAbsentFieldsStay()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(1795293677353 + 60_000));

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal(0.65m, charge.BatteryLevel);
        Assert.Equal(TeslaChargingState.Charging, charge.ChargingState);
        Assert.Equal(11000m, charge.ChargingPower);
        Assert.Equal(TimeSpan.FromMinutes(90), charge.RemainingChargingTime);
        Assert.Equal("IEC", charge.ChargeCable); // not in the snapshot, unchanged
    }

    [Fact]
    public void WhenCachedSnapshotIsOlder_ThenItIsIgnored()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(1795293677353 - 60_000));

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal(0.7m, charge.BatteryLevel);
    }
}
