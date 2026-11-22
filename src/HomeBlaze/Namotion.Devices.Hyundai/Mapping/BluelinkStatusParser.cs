// Derived from hyundai_kia_connect_api (MIT License, Copyright (c) 2021, Fuat Akgun); see THIRD-PARTY-NOTICES.md.
using System.Globalization;
using System.Text.Json;
using HomeBlaze.Abstractions.Devices.Locks;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Mapping;

/// <summary>
/// Parses the CCS2 status tree (<c>resMsg.state.Vehicle</c> of <c>/ccs2/carstatus/latest</c>), which the server returns
/// for both protocols. The rules follow <c>_update_vehicle_properties_ccs2</c> of hyundai_kia_connect_api. A missing core
/// field, an unknown code or a core value in an unexpected format is logged through <see cref="BluelinkApiChanges"/>.
/// </summary>
internal static class BluelinkStatusParser
{
    private const decimal MilesToKilometers = 1.609344m;
    private const decimal PsiToBar = 0.0689476m;

    public static BluelinkVehicleStatus Parse(JsonElement vehicle, ILogger logger)
    {
        var chargingState = vehicle.GetInt32("Green.ChargingInformation.Charging.State");
        var fastening = GetCode(vehicle, "Green.ChargingInformation.ConnectorFastening.State", 1, logger);
        var remainingMinutes = vehicle.GetDecimal("Green.ChargingInformation.Charging.RemainTime");
        var isCharging = GetIsCharging(chargingState, fastening, remainingMinutes);
        var pressureScale = GetPressureScale(vehicle, logger);
        var time = ParseTime(vehicle.GetString("Date"));
        if (time is null)
        {
            LogMissingOrUnexpected(vehicle, "Date", isRequired: true, logger);
        }

        return new BluelinkVehicleStatus
        {
            Time = time,
            TotalDrivenDistance = GetNumber(vehicle, "Drivetrain.Odometer", isRequired: true, logger),
            AuxiliaryBatteryLevel = vehicle.GetInt32("Electronics.Battery.SensorReliability") == 1 ? null : GetPercent(vehicle, "Electronics.Battery.Level"),

            BatteryLevel = ToPercent(GetNumber(vehicle, "Green.BatteryManagement.BatteryRemain.Ratio", isRequired: true, logger)),
            IsPluggedIn = GetIsPluggedIn(GetCode(vehicle, "Green.ChargingConnector.LockState", 5, logger), chargingState, fastening),
            IsCharging = isCharging,
            // Reported in kW.
            ChargingPower = vehicle.GetDecimal("Green.Electric.SmartGrid.RealTimePower") * 1000m,
            RemainingChargingTime = isCharging == true && remainingMinutes > 0 ? TimeSpan.FromMinutes((double)remainingMinutes.Value) : null,
            ChargeLimit = GetPercent(vehicle, "Green.ChargingInformation.TargetSoC.Standard"),
            FastChargeLimit = GetPercent(vehicle, "Green.ChargingInformation.TargetSoC.Quick"),
            Range = GetRange(vehicle, logger),

            IsClimateOn = vehicle.GetDecimal("Cabin.HVAC.Row1.Driver.Blower.SpeedLevel") is { } speed ? speed > 0 : null,
            DriverTemperatureSetting = GetCelsius(vehicle, "Cabin.HVAC.Row1.Driver.Temperature", logger),
            OutsideTemperature = GetCelsius(vehicle, "Cabin.HVAC.OutsideTemperature", logger),
            IsFrontDefrosterOn = GetOnOff(vehicle, "Body.Windshield.Front.Defog.State", logger),
            IsSteeringWheelHeaterOn = GetOnOff(vehicle, "Cabin.SteeringWheel.Heat.State", logger),

            LockState = GetLockState(
                IsDoorLocked(vehicle, "Cabin.Door.Row1.Driver.Lock", logger, isRequired: true),
                IsDoorLocked(vehicle, "Cabin.Door.Row1.Passenger.Lock", logger),
                IsDoorLocked(vehicle, "Cabin.Door.Row2.Left.Lock", logger),
                IsDoorLocked(vehicle, "Cabin.Door.Row2.Right.Lock", logger)),
            IsDriverFrontDoorOpen = vehicle.GetFlag("Cabin.Door.Row1.Driver.Open"),
            IsPassengerFrontDoorOpen = vehicle.GetFlag("Cabin.Door.Row1.Passenger.Open"),
            IsDriverRearDoorOpen = vehicle.GetFlag("Cabin.Door.Row2.Left.Open"),
            IsPassengerRearDoorOpen = vehicle.GetFlag("Cabin.Door.Row2.Right.Open"),
            IsTrunkOpen = vehicle.GetFlag("Body.Trunk.Open"),
            IsHoodOpen = vehicle.GetFlag("Body.Hood.Open"),
            IsDriverFrontWindowOpen = IsWindowOpen(vehicle, "Row1.Driver"),
            IsPassengerFrontWindowOpen = IsWindowOpen(vehicle, "Row1.Passenger"),
            IsDriverRearWindowOpen = IsWindowOpen(vehicle, "Row2.Left"),
            IsPassengerRearWindowOpen = IsWindowOpen(vehicle, "Row2.Right"),

            FrontLeftTire = GetTire(vehicle, "Row1.Left", pressureScale),
            FrontRightTire = GetTire(vehicle, "Row1.Right", pressureScale),
            RearLeftTire = GetTire(vehicle, "Row2.Left", pressureScale),
            RearRightTire = GetTire(vehicle, "Row2.Right", pressureScale)
        };
    }

    /// <summary>Parses the UTC time <c>yyyyMMddHHmmss</c>; a fraction of any length after the first 14 characters is ignored.</summary>
    internal static DateTimeOffset? ParseTime(string? value) =>
        value is { Length: >= 14 } &&
        DateTime.TryParseExact(value.AsSpan(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? new DateTimeOffset(time, TimeSpan.Zero)
            : null;

    private static bool? GetIsCharging(int? chargingState, int? fastening, decimal? remainingMinutes)
    {
        // A state of 0 or a disconnected connector overrule a stale remaining time.
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

    private static decimal? GetPercent(JsonElement vehicle, string path) => ToPercent(vehicle.GetDecimal(path));

    private static decimal? ToPercent(decimal? value) => value is { } percent and >= 0m and <= 100m ? percent / 100m : null;

    /// <summary>Reads a number; logs a value that is not a number, and a missing field when every status has it.</summary>
    private static decimal? GetNumber(JsonElement vehicle, string path, bool isRequired, ILogger logger)
    {
        var value = vehicle.GetDecimal(path);
        if (value is null)
        {
            LogMissingOrUnexpected(vehicle, path, isRequired, logger);
        }

        return value;
    }

    /// <summary>Reads a code and logs one outside 0 to <paramref name="maximum"/>; a required field also logs when missing or not a number.</summary>
    private static int? GetCode(JsonElement vehicle, string path, int maximum, ILogger logger, bool isRequired = false)
    {
        var code = vehicle.GetInt32(path);
        if (code is { } value)
        {
            if (value < 0 || value > maximum)
            {
                BluelinkApiChanges.LogUnknownCode(logger, path, value);
            }
        }
        else if (isRequired)
        {
            LogMissingOrUnexpected(vehicle, path, isRequired: true, logger);
        }

        return code;
    }

    private static void LogMissingOrUnexpected(JsonElement vehicle, string path, bool isRequired, ILogger logger)
    {
        if (vehicle.Find(path) is { } element)
        {
            BluelinkApiChanges.LogUnexpectedValue(logger, path, element);
        }
        else if (isRequired)
        {
            BluelinkApiChanges.LogMissing(logger, path);
        }
    }

    private static decimal? GetRange(JsonElement vehicle, ILogger logger)
    {
        // 4095 marks an invalid reading of the 12-bit field.
        if (GetNumber(vehicle, "Drivetrain.FuelSystem.DTE.Total", isRequired: false, logger) is not { } total || total is < 0m or > 4094m)
        {
            return null;
        }

        return GetCode(vehicle, "Drivetrain.FuelSystem.DTE.Unit", 3, logger) switch
        {
            1 => total,
            2 or 3 => Math.Round(total * MilesToKilometers, 1),
            _ => null
        };
    }

    /// <summary>Reads a temperature in °C (unit 0); "OFF" or another unit, such as °F (1), is null.</summary>
    private static decimal? GetCelsius(JsonElement vehicle, string path, ILogger logger) =>
        GetCode(vehicle, path + ".Unit", 1, logger) == 0 ? vehicle.GetDecimal(path + ".Value") : null;

    private static bool? GetOnOff(JsonElement vehicle, string path, ILogger logger) => GetCode(vehicle, path, 2, logger) switch
    {
        1 => true,
        0 or 2 => false,
        _ => null
    };

    /// <summary>Lock 1 means unlocked; hyundai_kia_connect_api reads a door as locked when Lock is 0.</summary>
    private static bool? IsDoorLocked(JsonElement vehicle, string path, ILogger logger, bool isRequired = false) => GetCode(vehicle, path, 1, logger, isRequired) switch
    {
        null => null,
        0 => true,
        _ => false
    };

    /// <summary>One door known unlocked decides Unlocked; otherwise any unknown door leaves the state unknown.</summary>
    private static DoorLockState? GetLockState(bool? frontLeft, bool? frontRight, bool? rearLeft, bool? rearRight)
    {
        if (frontLeft == false || frontRight == false || rearLeft == false || rearRight == false)
        {
            return DoorLockState.Unlocked;
        }

        if (frontLeft is null || frontRight is null || rearLeft is null || rearRight is null)
        {
            return null;
        }

        return DoorLockState.Locked;
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
    /// <remarks>Unit 3 is indirect tire pressure monitoring, which reports no pressure.</remarks>
    private static decimal? GetPressureScale(JsonElement vehicle, ILogger logger) => GetCode(vehicle, "Chassis.Axle.Tire.PressureUnit", 3, logger) switch
    {
        0 => PsiToBar,
        1 => 0.05m,
        2 => 0.1m,
        _ => null
    };

    private static BluelinkTireStatus GetTire(JsonElement vehicle, string position, decimal? pressureScale) => new(
        vehicle.GetFlag($"Chassis.Axle.{position}.Tire.PressureLow"),
        // 255 marks no reading.
        pressureScale is { } scale && vehicle.GetDecimal($"Chassis.Axle.{position}.Tire.Pressure") is { } raw and > 0m and not 255m
            ? Math.Round(raw * scale, 2)
            : null);
}
