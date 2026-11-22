using System.Globalization;
using System.Text.Json;
using HomeBlaze.Abstractions.Devices.Locks;
using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Mapping;

/// <summary>
/// Parses the CCS2 status tree (<c>resMsg.state.Vehicle</c> of <c>/ccs2/carstatus/latest</c>), which the server returns
/// for both protocols. The rules follow <c>_update_vehicle_properties_ccs2</c> of hyundai_kia_connect_api.
/// </summary>
internal static class BluelinkStatusParser
{
    private const decimal MilesToKilometers = 1.609344m;
    private const decimal PsiToBar = 0.0689476m;

    public static BluelinkVehicleStatus Parse(JsonElement vehicle)
    {
        var chargingState = vehicle.GetInt32("Green.ChargingInformation.Charging.State");
        var fastening = vehicle.GetInt32("Green.ChargingInformation.ConnectorFastening.State");
        var remainingMinutes = vehicle.GetDecimal("Green.ChargingInformation.Charging.RemainTime");
        var isCharging = GetIsCharging(chargingState, fastening, remainingMinutes);
        var pressureScale = GetPressureScale(vehicle);

        return new BluelinkVehicleStatus
        {
            Time = ParseTime(vehicle.GetString("Date")),
            TotalDrivenDistance = vehicle.GetDecimal("Drivetrain.Odometer"),
            AuxiliaryBatteryLevel = vehicle.GetInt32("Electronics.Battery.SensorReliability") == 1 ? null : GetPercent(vehicle, "Electronics.Battery.Level"),

            BatteryLevel = GetPercent(vehicle, "Green.BatteryManagement.BatteryRemain.Ratio"),
            IsPluggedIn = GetIsPluggedIn(vehicle.GetInt32("Green.ChargingConnector.LockState"), chargingState, fastening),
            IsCharging = isCharging,
            // Reported in kW.
            ChargingPower = vehicle.GetDecimal("Green.Electric.SmartGrid.RealTimePower") * 1000m,
            RemainingChargingTime = isCharging == true && remainingMinutes > 0 ? TimeSpan.FromMinutes((double)remainingMinutes.Value) : null,
            ChargeLimit = GetPercent(vehicle, "Green.ChargingInformation.TargetSoC.Standard"),
            FastChargeLimit = GetPercent(vehicle, "Green.ChargingInformation.TargetSoC.Quick"),
            Range = GetRange(vehicle),

            IsClimateOn = vehicle.GetDecimal("Cabin.HVAC.Row1.Driver.Blower.SpeedLevel") is { } speed ? speed > 0 : null,
            TemperatureSetting = GetCelsius(vehicle, "Cabin.HVAC.Row1.Driver.Temperature"),
            OutsideTemperature = GetCelsius(vehicle, "Cabin.HVAC.OutsideTemperature"),
            IsDefrostOn = GetOnOff(vehicle, "Body.Windshield.Front.Defog.State"),
            IsSteeringWheelHeaterOn = GetOnOff(vehicle, "Cabin.SteeringWheel.Heat.State"),

            LockState = GetLockState(
                IsDoorLocked(vehicle, "Row1.Driver"), IsDoorLocked(vehicle, "Row1.Passenger"),
                IsDoorLocked(vehicle, "Row2.Left"), IsDoorLocked(vehicle, "Row2.Right")),
            IsFrontLeftDoorOpen = vehicle.GetFlag("Cabin.Door.Row1.Driver.Open"),
            IsFrontRightDoorOpen = vehicle.GetFlag("Cabin.Door.Row1.Passenger.Open"),
            IsRearLeftDoorOpen = vehicle.GetFlag("Cabin.Door.Row2.Left.Open"),
            IsRearRightDoorOpen = vehicle.GetFlag("Cabin.Door.Row2.Right.Open"),
            IsTrunkOpen = vehicle.GetFlag("Body.Trunk.Open"),
            IsHoodOpen = vehicle.GetFlag("Body.Hood.Open"),
            IsFrontLeftWindowOpen = IsWindowOpen(vehicle, "Row1.Driver"),
            IsFrontRightWindowOpen = IsWindowOpen(vehicle, "Row1.Passenger"),
            IsRearLeftWindowOpen = IsWindowOpen(vehicle, "Row2.Left"),
            IsRearRightWindowOpen = IsWindowOpen(vehicle, "Row2.Right"),

            FrontLeftTire = GetTire(vehicle, "Row1.Left", pressureScale),
            FrontRightTire = GetTire(vehicle, "Row1.Right", pressureScale),
            RearLeftTire = GetTire(vehicle, "Row2.Left", pressureScale),
            RearRightTire = GetTire(vehicle, "Row2.Right", pressureScale)
        };
    }

    /// <summary>Parses the UTC time <c>yyyyMMddHHmmss</c>, optionally with milliseconds.</summary>
    internal static DateTimeOffset? ParseTime(string? value) =>
        DateTime.TryParseExact(value, ["yyyyMMddHHmmss.fff", "yyyyMMddHHmmss"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? new DateTimeOffset(time, TimeSpan.Zero)
            : null;

    private static bool? GetIsCharging(int? chargingState, int? fastening, decimal? remainingMinutes)
    {
        // A state of 0 or a disconnected connector outrule a stale remaining time.
        if (chargingState == 0 || fastening == 0)
        {
            return false;
        }

        return remainingMinutes switch
        {
            0m => false,
            > 0m => true,
            _ => null
        };
    }

    private static bool? GetIsPluggedIn(int? connectorLockState, int? chargingState, int? fastening) => fastening switch
    {
        1 => true,
        // Fastening 0 means unplugged only while the connector lock is idle; a locked, idle plug reads 0 as well.
        0 when connectorLockState == 0 => false,
        // Hyundai omits the connector lock; there, fastening 0 follows the cable while the car does not charge.
        0 when connectorLockState is null && chargingState == 0 => false,
        _ => null
    };

    private static decimal? GetPercent(JsonElement vehicle, string path) =>
        vehicle.GetDecimal(path) is { } percent and >= 0m and <= 100m ? percent / 100m : null;

    private static decimal? GetRange(JsonElement vehicle)
    {
        // 4095 marks an invalid reading of the 12-bit field.
        if (vehicle.GetDecimal("Drivetrain.FuelSystem.DTE.Total") is not { } total || total is < 0m or > 4094m)
        {
            return null;
        }

        return vehicle.GetInt32("Drivetrain.FuelSystem.DTE.Unit") switch
        {
            1 => total,
            2 => Math.Round(total * MilesToKilometers, 1),
            _ => null
        };
    }

    /// <summary>Reads a temperature in °C (unit 0); "OFF" or another unit is null.</summary>
    private static decimal? GetCelsius(JsonElement vehicle, string path) =>
        vehicle.GetInt32(path + ".Unit") == 0 ? vehicle.GetDecimal(path + ".Value") : null;

    private static bool? GetOnOff(JsonElement vehicle, string path) => vehicle.GetInt32(path) switch
    {
        1 => true,
        0 or 2 => false,
        _ => null
    };

    /// <summary>Lock 1 means unlocked; hyundai_kia_connect_api reads a door as locked when Lock is 0.</summary>
    private static bool? IsDoorLocked(JsonElement vehicle, string door) => vehicle.GetInt32($"Cabin.Door.{door}.Lock") switch
    {
        null => null,
        0 => true,
        _ => false
    };

    private static DoorLockState? GetLockState(params bool?[] doorsLocked)
    {
        if (doorsLocked.Any(isLocked => isLocked is null))
        {
            return null;
        }

        return doorsLocked.All(isLocked => isLocked == true) ? DoorLockState.Locked : DoorLockState.Unlocked;
    }

    /// <summary>A vented window reports Open 0 with OpenLevel above 0 and counts as open.</summary>
    private static bool? IsWindowOpen(JsonElement vehicle, string window)
    {
        var open = vehicle.GetInt32($"Cabin.Window.{window}.Open");
        var openLevel = vehicle.GetInt32($"Cabin.Window.{window}.OpenLevel");
        if (open is null && openLevel is null)
        {
            return null;
        }

        return open > 0 || openLevel > 0;
    }

    /// <summary>The factor from the raw pressure to bar: psi (0) is whole psi, kPa (1) counts 5 kPa steps, bar (2) counts 0.1 bar steps.</summary>
    private static decimal? GetPressureScale(JsonElement vehicle) => vehicle.GetInt32("Chassis.Axle.Tire.PressureUnit") switch
    {
        0 => PsiToBar,
        1 => 0.05m,
        2 => 0.1m,
        _ => null
    };

    private static BluelinkTireStatus GetTire(JsonElement vehicle, string position, decimal? pressureScale) => new(
        vehicle.GetFlag($"Chassis.Axle.{position}.Tire.PressureLow"),
        pressureScale is { } scale && vehicle.GetDecimal($"Chassis.Axle.{position}.Tire.Pressure") is { } raw and > 0m
            ? Math.Round(raw * scale, 2)
            : null);
}
