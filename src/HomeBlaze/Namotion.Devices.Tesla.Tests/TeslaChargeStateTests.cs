using CarServer;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaChargeStateTests
{
    private static TeslaChargeState CreateFromFixture()
    {
        var data = FixtureLoader.LoadVehicleData();
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
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp + 60_000));

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal(0.65m, charge.BatteryLevel);
        Assert.Equal(TeslaChargingState.Charging, charge.ChargingState);
        Assert.Equal(11000m, charge.ChargingPower);
        Assert.Equal(TimeSpan.FromMinutes(90), charge.RemainingChargingTime);
        Assert.Equal("IEC", charge.ChargeCable); // not in the snapshot, unchanged
        Assert.Equal(0.5m, charge.MinimumChargeLimit); // not in the snapshot, unchanged
        Assert.Equal(6m, charge.RequestedChargingCurrent); // not in the snapshot, unchanged
        Assert.Equal(TeslaChargePortLatch.Engaged, charge.ChargePortLatch); // not in the snapshot, unchanged
    }

    [Fact]
    public void WhenCachedSnapshotIsOlder_ThenItIsIgnored()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp - 60_000));

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal(0.7m, charge.BatteryLevel);
    }

    [Fact]
    public void WhenSnapshotHasEqualTimestamp_ThenItApplies()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp));

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal(0.65m, charge.BatteryLevel);
    }

    [Fact]
    public void WhenJsonIsOlderThanAppliedSnapshot_ThenItIsIgnored()
    {
        // Arrange
        var charge = new TeslaChargeState(NullLogger.Instance);
        charge.Update(VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp + 60_000)).ChargeState);
        var data = FixtureLoader.LoadVehicleData();

        // Act
        charge.Update(data.ChargeState!);

        // Assert
        Assert.Equal(0.65m, charge.BatteryLevel);
        Assert.Equal(TeslaChargingState.Charging, charge.ChargingState);
    }

    [Fact]
    public void WhenFixtureIsApplied_ThenEstimatedBatteryCapacityUsesIdealRanges()
    {
        // Act
        var charge = CreateFromFixture();

        // Assert
        var expected = 230.45m / 0.69m / 166.5m * 36640m;
        Assert.Equal(expected, charge.EstimatedBatteryCapacity!.Value, 0);
    }

    [Fact]
    public void WhenFixtureIsApplied_ThenRemainingJsonFieldsAreConverted()
    {
        // Act
        var charge = CreateFromFixture();

        // Assert
        Assert.Equal(Math.Round(166.5m * 1.609344m, 3), Math.Round(charge.AddedIdealRange!.Value, 3));
        Assert.Equal(0m, charge.ChargeRate);
        Assert.Equal(6m, charge.ChargingCurrentSetting);
        Assert.False(charge.IsChargeEnabled);
        Assert.Null(charge.IsChargeEnabledByUser);
        Assert.False(charge.IsSuperchargerSessionFromTripPlanner);
        Assert.Equal(TeslaScheduleDays.AllWeek, charge.OffPeakChargingDays);
        Assert.Equal(TeslaScheduleDays.Weekdays, charge.PreconditioningDays);
        Assert.Null(charge.OffPeakChargingEndTime);
        Assert.Null(charge.ScheduledDepartureTime);
    }

    [Fact]
    public void WhenSnapshotHasEnumFields_ThenTheyMatchTheJsonValues()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp + 60_000));
        snapshot.ChargeState.ConnChargeCable = new ChargeState.Types.CableType { IEC = new CarServer.Void() };
        snapshot.ChargeState.ChargePortLatch = new ChargePortLatchState { Disengaged = new CarServer.Void() };
        snapshot.ChargeState.FastChargerType = new ChargeState.Types.ChargerType { Combo = new CarServer.Void() };
        snapshot.ChargeState.FastChargerBrand = new ChargeState.Types.ChargerBrand { Tesla = new CarServer.Void() };
        snapshot.ChargeState.ScheduledChargingMode = ChargeState.Types.ScheduledChargingMode.DepartBy;
        snapshot.ChargeState.ChargePortColor = ChargeState.Types.ChargePortColor_E.ChargePortColorFlashingGreen;
        snapshot.ChargeState.ScheduledDepartureTime = snapshot.ChargeState.Timestamp;
        snapshot.ChargeState.ScheduledDepartureTimeMinutes = 450;
        snapshot.ChargeState.OffPeakChargingTimes = new OffPeakChargingTimes { Weekdays = new CarServer.Void() };
        snapshot.ChargeState.OffPeakHoursEndTime = 360;
        snapshot.ChargeState.PreconditioningTimes = new PreconditioningTimes { AllWeek = new CarServer.Void() };

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal("IEC", charge.ChargeCable);
        Assert.Equal(TeslaChargePortLatch.Disengaged, charge.ChargePortLatch);
        Assert.Equal("Combo", charge.FastChargerType);
        Assert.Equal("Tesla", charge.FastChargerBrand);
        Assert.Equal(TeslaScheduledChargingMode.DepartBy, charge.ScheduledChargingMode);
        Assert.Equal("FlashingGreen", charge.ChargePortColor);
        Assert.Equal(TimeSpan.FromMinutes(450), charge.ScheduledDepartureTime);
        Assert.Equal(TeslaScheduleDays.AllWeek, charge.OffPeakChargingDays); // only the JSON decides
        Assert.Equal(TimeSpan.FromHours(6), charge.OffPeakChargingEndTime);
        Assert.Equal(TeslaScheduleDays.AllWeek, charge.PreconditioningDays);
    }

    [Fact]
    public void WhenSnapshotReportsSna_ThenValuesStay()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp + 60_000));
        snapshot.ChargeState.ConnChargeCable = new ChargeState.Types.CableType { SNA = new CarServer.Void() };
        snapshot.ChargeState.ChargePortLatch = new ChargePortLatchState { SNA = new CarServer.Void() };
        snapshot.ChargeState.FastChargerBrand = new ChargeState.Types.ChargerBrand { SNA = new CarServer.Void() };

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal("IEC", charge.ChargeCable);
        Assert.Equal(TeslaChargePortLatch.Engaged, charge.ChargePortLatch);
        Assert.Null(charge.FastChargerBrand); // "<invalid>" in the JSON, so the same as before
    }

    [Fact]
    public void WhenSnapshotHasBothRemainingTimes_ThenChargeLimitTimeWins()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp + 60_000));
        snapshot.ChargeState.MinutesToFullCharge = 120;

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(90), charge.RemainingChargingTime);
    }

    [Fact]
    public void WhenSnapshotHasOnlyMinutesToFullCharge_ThenItIsUsed()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp + 60_000));
        snapshot.ChargeState.ClearMinutesToChargeLimit();
        snapshot.ChargeState.MinutesToFullCharge = 120;

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(120), charge.RemainingChargingTime);
    }

    [Fact]
    public void WhenSnapshotHasNonFiniteFloat_ThenValueBecomesNullWithoutThrowing()
    {
        // Arrange
        var charge = CreateFromFixture();
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp + 60_000));
        snapshot.ChargeState.BatteryRange = float.NaN;

        // Act
        charge.Update(snapshot.ChargeState);

        // Assert
        Assert.Null(charge.Range);
    }

    [Fact]
    public void WhenJsonAndSnapshotDescribeTheSameRanges_ThenKilometersAreEqual()
    {
        // Arrange
        var jsonCharge = new TeslaChargeState(NullLogger.Instance);
        jsonCharge.Update(new Client.Dto.TeslaChargeStateDto
        {
            BatteryRange = 230.45m,
            IdealBatteryRange = 288.07m,
            EstBatteryRange = 201.33m,
            ChargeMilesAddedRated = 12.35m,
            ChargeMilesAddedIdeal = 15.44m,
            Timestamp = FixtureLoader.FixtureTimestamp
        });
        var snapshot = VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureLoader.FixtureTimestamp));
        snapshot.ChargeState.BatteryRange = 230.4537f;
        snapshot.ChargeState.IdealBatteryRange = 288.0749f;
        snapshot.ChargeState.EstBatteryRange = 201.3349f;
        snapshot.ChargeState.ChargeMilesAddedRated = 12.3512f;
        snapshot.ChargeState.ChargeMilesAddedIdeal = 15.4388f;
        var protobufCharge = new TeslaChargeState(NullLogger.Instance);

        // Act
        protobufCharge.Update(snapshot.ChargeState);

        // Assert
        Assert.NotNull(jsonCharge.Range);
        Assert.Equal(jsonCharge.Range, protobufCharge.Range);
        Assert.Equal(jsonCharge.IdealRange, protobufCharge.IdealRange);
        Assert.Equal(jsonCharge.EstimatedRange, protobufCharge.EstimatedRange);
        Assert.Equal(jsonCharge.AddedRange, protobufCharge.AddedRange);
        Assert.Equal(jsonCharge.AddedIdealRange, protobufCharge.AddedIdealRange);
    }
}
