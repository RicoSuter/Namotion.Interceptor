using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaClimateStateDto
{
    [JsonPropertyName("allow_cabin_overheat_protection")] public bool? AllowCabinOverheatProtection { get; init; }
    [JsonPropertyName("auto_seat_climate_left")] public bool? AutoSeatClimateLeft { get; init; }
    [JsonPropertyName("auto_seat_climate_right")] public bool? AutoSeatClimateRight { get; init; }
    [JsonPropertyName("auto_steering_wheel_heat")] public bool? AutoSteeringWheelHeat { get; init; }
    [JsonPropertyName("battery_heater")] public bool? BatteryHeater { get; init; }
    [JsonPropertyName("battery_heater_no_power")] public bool? BatteryHeaterNoPower { get; init; }
    [JsonPropertyName("cabin_overheat_protection")] public string? CabinOverheatProtection { get; init; }
    [JsonPropertyName("cabin_overheat_protection_actively_cooling")] public bool? CabinOverheatProtectionActivelyCooling { get; init; }
    [JsonPropertyName("climate_keeper_mode")] public string? ClimateKeeperMode { get; init; }
    [JsonPropertyName("cop_activation_temperature")] public string? CopActivationTemperature { get; init; }
    [JsonPropertyName("defrost_mode")] public int? DefrostMode { get; init; }
    [JsonPropertyName("driver_temp_setting")] public decimal? DriverTempSetting { get; init; }
    [JsonPropertyName("fan_status")] public int? FanStatus { get; init; }
    [JsonPropertyName("hvac_auto_request")] public string? HvacAutoRequest { get; init; }
    [JsonPropertyName("inside_temp")] public decimal? InsideTemp { get; init; }
    [JsonPropertyName("is_auto_conditioning_on")] public bool? IsAutoConditioningOn { get; init; }
    [JsonPropertyName("is_climate_on")] public bool? IsClimateOn { get; init; }
    [JsonPropertyName("is_front_defroster_on")] public bool? IsFrontDefrosterOn { get; init; }
    [JsonPropertyName("is_preconditioning")] public bool? IsPreconditioning { get; init; }
    [JsonPropertyName("is_rear_defroster_on")] public bool? IsRearDefrosterOn { get; init; }
    [JsonPropertyName("left_temp_direction")] public int? LeftTempDirection { get; init; }
    [JsonPropertyName("max_avail_temp")] public decimal? MaxAvailTemp { get; init; }
    [JsonPropertyName("min_avail_temp")] public decimal? MinAvailTemp { get; init; }
    [JsonPropertyName("outside_temp")] public decimal? OutsideTemp { get; init; }
    [JsonPropertyName("passenger_temp_setting")] public decimal? PassengerTempSetting { get; init; }
    [JsonPropertyName("remote_heater_control_enabled")] public bool? RemoteHeaterControlEnabled { get; init; }
    [JsonPropertyName("right_temp_direction")] public int? RightTempDirection { get; init; }
    [JsonPropertyName("seat_heater_left")] public int? SeatHeaterLeft { get; init; }
    [JsonPropertyName("seat_heater_rear_center")] public int? SeatHeaterRearCenter { get; init; }
    [JsonPropertyName("seat_heater_rear_left")] public int? SeatHeaterRearLeft { get; init; }
    [JsonPropertyName("seat_heater_rear_right")] public int? SeatHeaterRearRight { get; init; }
    [JsonPropertyName("seat_heater_right")] public int? SeatHeaterRight { get; init; }
    [JsonPropertyName("side_mirror_heaters")] public bool? SideMirrorHeaters { get; init; }
    [JsonPropertyName("steering_wheel_heat_level")] public int? SteeringWheelHeatLevel { get; init; }
    [JsonPropertyName("steering_wheel_heater")] public bool? SteeringWheelHeater { get; init; }
    [JsonPropertyName("supports_fan_only_cabin_overheat_protection")] public bool? SupportsFanOnlyCabinOverheatProtection { get; init; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
    [JsonPropertyName("wiper_blade_heater")] public bool? WiperBladeHeater { get; init; }
}
