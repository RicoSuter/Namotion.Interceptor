using System.Text.Json;
using HomeBlaze.Abstractions.Devices.Locks;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Devices.Hyundai.Tests.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkStatusParserTests
{
    private static BluelinkVehicleStatus ParseFixture()
    {
        using var document = JsonDocument.Parse(FixtureLoader.Load("carstatus-legacy-ev.json"));
        return BluelinkStatusParser.Parse(document.RootElement.Find("resMsg.state.Vehicle")!.Value);
    }

    private static BluelinkVehicleStatus Parse(string vehicleJson)
    {
        using var document = JsonDocument.Parse(vehicleJson);
        return BluelinkStatusParser.Parse(document.RootElement);
    }

    [Fact]
    public void WhenCarCharges_ThenChargeValuesAreRead()
    {
        // Act
        var status = ParseFixture();

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 15, 0, TimeSpan.Zero), status.Time);
        Assert.Equal(0.465m, status.BatteryLevel);
        Assert.True(status.IsPluggedIn);
        Assert.True(status.IsCharging);
        Assert.Equal(7200m, status.ChargingPower);
        Assert.Equal(TimeSpan.FromMinutes(95), status.RemainingChargingTime);
        Assert.Equal(0.8m, status.ChargeLimit);
        Assert.Equal(1.0m, status.FastChargeLimit);
        Assert.Equal(190m, status.Range);
        Assert.Equal(16773.9m, status.TotalDrivenDistance);
        Assert.Equal(0.84m, status.AuxiliaryBatteryLevel);
    }

    [Fact]
    public void WhenClimateIsOff_ThenTemperatureSettingIsNull()
    {
        // Act
        var status = ParseFixture();

        // Assert
        Assert.False(status.IsClimateOn);
        Assert.Null(status.TemperatureSetting);
        Assert.False(status.IsDefrostOn);
        Assert.False(status.IsSteeringWheelHeaterOn);
        Assert.Null(status.OutsideTemperature);
    }

    [Fact]
    public void WhenAllDoorsReportLockZero_ThenVehicleIsLocked()
    {
        // Act
        var status = ParseFixture();

        // Assert
        Assert.Equal(DoorLockState.Locked, status.LockState);
        Assert.True(status.IsRearRightDoorOpen);
        Assert.False(status.IsFrontLeftDoorOpen);
        Assert.True(status.IsFrontRightWindowOpen);
        Assert.False(status.IsTrunkOpen);
    }

    [Fact]
    public void WhenPressureUnitIsBar_ThenPressuresAreScaled()
    {
        // Act
        var status = ParseFixture();

        // Assert
        Assert.Equal(new BluelinkTireStatus(false, 2.5m), status.FrontLeftTire);
        Assert.Equal(new BluelinkTireStatus(true, 2.2m), status.FrontRightTire);
    }

    [Fact]
    public void WhenOneDoorReportsLockOne_ThenVehicleIsUnlocked()
    {
        // Act
        var status = Parse("""{"Cabin":{"Door":{"Row1":{"Driver":{"Lock":1},"Passenger":{"Lock":0}},"Row2":{"Left":{"Lock":0},"Right":{"Lock":0}}}}}""");

        // Assert
        Assert.Equal(DoorLockState.Unlocked, status.LockState);
    }

    [Fact]
    public void WhenADoorLockIsMissing_ThenLockStateIsUnknown()
    {
        // Act
        var status = Parse("""{"Cabin":{"Door":{"Row1":{"Driver":{"Lock":0}}}}}""");

        // Assert
        Assert.Null(status.LockState);
    }

    [Theory]
    [InlineData("""{"Drivetrain":{"FuelSystem":{"DTE":{"Unit":1,"Total":4095}}}}""", null)]
    [InlineData("""{"Drivetrain":{"FuelSystem":{"DTE":{"Unit":2,"Total":100}}}}""", 160.9)]
    [InlineData("""{"Drivetrain":{"FuelSystem":{"DTE":{"Unit":0,"Total":100}}}}""", null)]
    public void WhenRangeIsASentinelOrInMiles_ThenItIsUnknownOrConverted(string json, double? expected)
    {
        // Act
        var status = Parse(json);

        // Assert
        Assert.Equal((decimal?)expected, status.Range);
    }

    [Fact]
    public void WhenAuxiliaryBatterySensorIsUnreliable_ThenLevelIsUnknown()
    {
        // Act
        var status = Parse("""{"Electronics":{"Battery":{"Level":84,"SensorReliability":1}}}""");

        // Assert
        Assert.Null(status.AuxiliaryBatteryLevel);
    }

    [Fact]
    public void WhenChargingStateIsZero_ThenNotChargingAndNoRemainingTime()
    {
        // Act
        var status = Parse("""{"Green":{"ChargingInformation":{"Charging":{"State":0,"RemainTime":40},"ConnectorFastening":{"State":0}}}}""");

        // Assert
        Assert.False(status.IsCharging);
        Assert.Null(status.RemainingChargingTime);
        Assert.False(status.IsPluggedIn);
    }

    [Fact]
    public void WhenFasteningIsZeroWithoutChargingState_ThenPluggedInIsUnknown()
    {
        // Act
        var status = Parse("""{"Green":{"ChargingInformation":{"Charging":{"RemainTime":0},"ConnectorFastening":{"State":0}}}}""");

        // Assert
        Assert.Null(status.IsPluggedIn);
        Assert.False(status.IsCharging);
    }

    [Fact]
    public void WhenClimateRuns_ThenSettingIsRead()
    {
        // Act
        var status = Parse("""{"Cabin":{"HVAC":{"Row1":{"Driver":{"Blower":{"SpeedLevel":3},"Temperature":{"Value":"22.0","Unit":0}}},"OutsideTemperature":{"Value":4,"Unit":0}}}}""");

        // Assert
        Assert.True(status.IsClimateOn);
        Assert.Equal(22.0m, status.TemperatureSetting);
        Assert.Equal(4m, status.OutsideTemperature);
    }

    [Fact]
    public void WhenPressureUnitIsUnknown_ThenPressureIsNullButWarningIsKept()
    {
        // Act
        var status = Parse("""{"Chassis":{"Axle":{"Tire":{"PressureUnit":3},"Row1":{"Left":{"Tire":{"PressureLow":1,"Pressure":0}}}}}}""");

        // Assert
        Assert.Equal(new BluelinkTireStatus(true, null), status.FrontLeftTire);
    }
}
