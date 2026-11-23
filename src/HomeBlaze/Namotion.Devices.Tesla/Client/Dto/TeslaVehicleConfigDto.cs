using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaVehicleConfigDto
{
    [JsonPropertyName("aux_park_lamps")] public string? AuxParkLamps { get; init; }
    [JsonPropertyName("badge_version")] public int? BadgeVersion { get; init; }
    [JsonPropertyName("can_accept_navigation_requests")] public bool? CanAcceptNavigationRequests { get; init; }
    [JsonPropertyName("can_actuate_trunks")] public bool? CanActuateTrunks { get; init; }
    [JsonPropertyName("car_special_type")] public string? CarSpecialType { get; init; }
    [JsonPropertyName("car_type")] public string? CarType { get; init; }
    [JsonPropertyName("charge_port_type")] public string? ChargePortType { get; init; }
    [JsonPropertyName("cop_user_set_temp_supported")] public bool? CopUserSetTempSupported { get; init; }
    [JsonPropertyName("dashcam_clip_save_supported")] public bool? DashcamClipSaveSupported { get; init; }
    [JsonPropertyName("default_charge_to_max")] public bool? DefaultChargeToMax { get; init; }
    [JsonPropertyName("driver_assist")] public string? DriverAssist { get; init; }
    [JsonPropertyName("ece_restrictions")] public bool? EceRestrictions { get; init; }
    [JsonPropertyName("efficiency_package")] public string? EfficiencyPackage { get; init; }
    [JsonPropertyName("eu_vehicle")] public bool? EuVehicle { get; init; }
    [JsonPropertyName("exterior_color")] public string? ExteriorColor { get; init; }
    [JsonPropertyName("exterior_trim")] public string? ExteriorTrim { get; init; }
    [JsonPropertyName("exterior_trim_override")] public string? ExteriorTrimOverride { get; init; }
    [JsonPropertyName("has_air_suspension")] public bool? HasAirSuspension { get; init; }
    [JsonPropertyName("has_ludicrous_mode")] public bool? HasLudicrousMode { get; init; }
    [JsonPropertyName("has_seat_cooling")] public bool? HasSeatCooling { get; init; }
    [JsonPropertyName("headlamp_type")] public string? HeadlampType { get; init; }
    [JsonPropertyName("interior_trim_type")] public string? InteriorTrimType { get; init; }
    [JsonPropertyName("key_version")] public int? KeyVersion { get; init; }
    [JsonPropertyName("motorized_charge_port")] public bool? MotorizedChargePort { get; init; }
    [JsonPropertyName("paint_color_override")] public string? PaintColorOverride { get; init; }
    [JsonPropertyName("performance_package")] public string? PerformancePackage { get; init; }
    [JsonPropertyName("plg")] public bool? Plg { get; init; }
    [JsonPropertyName("pws")] public bool? Pws { get; init; }
    [JsonPropertyName("rear_drive_unit")] public string? RearDriveUnit { get; init; }
    [JsonPropertyName("rear_seat_heaters")] public int? RearSeatHeaters { get; init; }
    [JsonPropertyName("rear_seat_type")] public int? RearSeatType { get; init; }
    [JsonPropertyName("rhd")] public bool? Rhd { get; init; }
    [JsonPropertyName("roof_color")] public string? RoofColor { get; init; }
    [JsonPropertyName("seat_type")] public int? SeatType { get; init; }
    [JsonPropertyName("sentry_preview_supported")] public bool? SentryPreviewSupported { get; init; }
    [JsonPropertyName("spoiler_type")] public string? SpoilerType { get; init; }
    [JsonPropertyName("sun_roof_installed")] public int? SunRoofInstalled { get; init; }
    [JsonPropertyName("supports_qr_pairing")] public bool? SupportsQrPairing { get; init; }
    [JsonPropertyName("third_row_seats")] public string? ThirdRowSeats { get; init; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
    [JsonPropertyName("trim_badging")] public string? TrimBadging { get; init; }
    [JsonPropertyName("use_range_badging")] public bool? UseRangeBadging { get; init; }
    [JsonPropertyName("utc_offset")] public int? UtcOffset { get; init; }
    [JsonPropertyName("webcam_interior_only")] public bool? WebcamInteriorOnly { get; init; }
    [JsonPropertyName("webcam_selfie_supported")] public bool? WebcamSelfieSupported { get; init; }
    [JsonPropertyName("webcam_supported")] public bool? WebcamSupported { get; init; }
    [JsonPropertyName("wheel_caps_on")] public bool? WheelCapsOn { get; init; }
    [JsonPropertyName("wheel_type")] public string? WheelType { get; init; }
}
