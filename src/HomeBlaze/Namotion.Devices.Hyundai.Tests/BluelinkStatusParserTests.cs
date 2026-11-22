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
    public void WhenAllDoorsReportLockZero_ThenVehicleIsLockedAndOpeningsAreRead()
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
    [InlineData("""{"Drivetrain":{"FuelSystem":{"DTE":{"Unit":3,"Total":100}}}}""", 160.9)]
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

    [Theory]
    [InlineData(0, 38, 2.62)]
    [InlineData(1, 51, 2.55)]
    public void WhenPressureUnitIsPsiOrKilopascal_ThenPressureIsConvertedToBar(int unit, int raw, double expectedBar)
    {
        // Act
        var status = Parse("""{"Chassis":{"Axle":{"Tire":{"PressureUnit":UNIT},"Row1":{"Left":{"Tire":{"PressureLow":0,"Pressure":RAW}}}}}}"""
            .Replace("UNIT", unit.ToString()).Replace("RAW", raw.ToString()));

        // Assert
        Assert.Equal(new BluelinkTireStatus(false, (decimal)expectedBar), status.FrontLeftTire);
    }

    [Fact]
    public void WhenPressureIs255_ThenPressureIsNullButWarningIsKept()
    {
        // Act
        var status = Parse("""{"Chassis":{"Axle":{"Tire":{"PressureUnit":2},"Row1":{"Left":{"Tire":{"PressureLow":1,"Pressure":255}}}}}}""");

        // Assert
        Assert.Equal(new BluelinkTireStatus(true, null), status.FrontLeftTire);
    }

    [Fact]
    public void WhenRangeIsAString_ThenItIsRead()
    {
        // Act
        var status = Parse("""{"Drivetrain":{"FuelSystem":{"DTE":{"Unit":1,"Total":"190"}}}}""");

        // Assert
        Assert.Equal(190m, status.Range);
    }

    [Fact]
    public void WhenConnectorLockIsIdleAndFasteningIsZero_ThenNotPluggedIn()
    {
        // Act
        var status = Parse("""{"Green":{"ChargingConnector":{"LockState":0},"ChargingInformation":{"ConnectorFastening":{"State":0}}}}""");

        // Assert
        Assert.False(status.IsPluggedIn);
    }

    [Fact]
    public void WhenConnectorIsLockedAndFasteningIsZero_ThenPluggedInIsUnknown()
    {
        // Act
        var status = Parse("""{"Green":{"ChargingConnector":{"LockState":2},"ChargingInformation":{"ConnectorFastening":{"State":0}}}}""");

        // Assert
        Assert.Null(status.IsPluggedIn);
    }

    [Theory]
    [InlineData("20261122101500", true)]
    [InlineData("20261122101500.12", true)]
    [InlineData("20261122101500.123", true)]
    [InlineData("2026112210", false)]
    [InlineData("not a time!!!", false)]
    [InlineData(null, false)]
    public void WhenTimeIsParsed_ThenAFractionOfAnyLengthIsIgnoredAndInvalidValuesAreNull(string? value, bool isValid)
    {
        // Act
        var time = BluelinkStatusParser.ParseTime(value);

        // Assert
        Assert.Equal(isValid ? new DateTimeOffset(2026, 11, 22, 10, 15, 0, TimeSpan.Zero) : null, time);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(5, null)]
    public void WhenDefrostStateIsTwoOrUnknown_ThenItIsOffOrNull(int state, bool? expected)
    {
        // Act
        var status = Parse("""{"Body":{"Windshield":{"Front":{"Defog":{"State":STATE}}}}}""".Replace("STATE", state.ToString()));

        // Assert
        Assert.Equal(expected, status.IsDefrostOn);
    }

    [Fact]
    public void WhenAuxiliaryBatteryLevelIs255_ThenLevelIsUnknown()
    {
        // Act
        var status = Parse("""{"Electronics":{"Battery":{"Level":255,"SensorReliability":0}}}""");

        // Assert
        Assert.Null(status.AuxiliaryBatteryLevel);
    }

    [Fact]
    public void WhenBatteryRatioIsAbove100_ThenBatteryLevelIsUnknown()
    {
        // Act
        var status = Parse("""{"Green":{"BatteryManagement":{"BatteryRemain":{"Ratio":120}}}}""");

        // Assert
        Assert.Null(status.BatteryLevel);
    }

    [Fact]
    public void WhenBothWindowFieldsAreMissing_ThenWindowStateIsUnknown()
    {
        // Act
        var status = Parse("""{"Cabin":{"Window":{"Row1":{"Driver":{}}}}}""");

        // Assert
        Assert.Null(status.IsFrontLeftWindowOpen);
    }

    [Fact]
    public void WhenOneDoorIsUnlockedAndAnotherIsMissing_ThenVehicleIsUnlocked()
    {
        // Act
        var status = Parse("""{"Cabin":{"Door":{"Row1":{"Driver":{"Lock":1}}}}}""");

        // Assert
        Assert.Equal(DoorLockState.Unlocked, status.LockState);
    }
}
