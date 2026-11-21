using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaVehicleStateDto
{
    [JsonPropertyName("api_version")] public int? ApiVersion { get; init; }
    [JsonPropertyName("autopark_state_v2")] public string? AutoparkStateV2 { get; init; }
    [JsonPropertyName("calendar_supported")] public bool? CalendarSupported { get; init; }
    [JsonPropertyName("car_version")] public string? CarVersion { get; init; }
    [JsonPropertyName("center_display_state")] public int? CenterDisplayState { get; init; }
    [JsonPropertyName("dashcam_clip_save_available")] public bool? DashcamClipSaveAvailable { get; init; }
    [JsonPropertyName("dashcam_state")] public string? DashcamState { get; init; }
    [JsonPropertyName("df")] public int? Df { get; init; }
    [JsonPropertyName("dr")] public int? Dr { get; init; }
    [JsonPropertyName("pf")] public int? Pf { get; init; }
    [JsonPropertyName("pr")] public int? Pr { get; init; }
    [JsonPropertyName("ft")] public int? Ft { get; init; }
    [JsonPropertyName("rt")] public int? Rt { get; init; }
    [JsonPropertyName("fd_window")] public int? FdWindow { get; init; }
    [JsonPropertyName("fp_window")] public int? FpWindow { get; init; }
    [JsonPropertyName("rd_window")] public int? RdWindow { get; init; }
    [JsonPropertyName("rp_window")] public int? RpWindow { get; init; }
    [JsonPropertyName("feature_bitmask")] public string? FeatureBitmask { get; init; }
    [JsonPropertyName("homelink_nearby")] public bool? HomelinkNearby { get; init; }
    [JsonPropertyName("is_user_present")] public bool? IsUserPresent { get; init; }
    [JsonPropertyName("locked")] public bool? Locked { get; init; }
    [JsonPropertyName("notifications_supported")] public bool? NotificationsSupported { get; init; }
    [JsonPropertyName("odometer")] public decimal? Odometer { get; init; }
    [JsonPropertyName("parsed_calendar_supported")] public bool? ParsedCalendarSupported { get; init; }
    [JsonPropertyName("remote_start")] public bool? RemoteStart { get; init; }
    [JsonPropertyName("remote_start_enabled")] public bool? RemoteStartEnabled { get; init; }
    [JsonPropertyName("remote_start_supported")] public bool? RemoteStartSupported { get; init; }
    [JsonPropertyName("santa_mode")] public int? SantaMode { get; init; }
    [JsonPropertyName("sentry_mode")] public bool? SentryMode { get; init; }
    [JsonPropertyName("sentry_mode_available")] public bool? SentryModeAvailable { get; init; }
    [JsonPropertyName("service_mode")] public bool? ServiceMode { get; init; }
    [JsonPropertyName("service_mode_plus")] public bool? ServiceModePlus { get; init; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
    [JsonPropertyName("tpms_hard_warning_fl")] public bool? TpmsHardWarningFl { get; init; }
    [JsonPropertyName("tpms_hard_warning_fr")] public bool? TpmsHardWarningFr { get; init; }
    [JsonPropertyName("tpms_hard_warning_rl")] public bool? TpmsHardWarningRl { get; init; }
    [JsonPropertyName("tpms_hard_warning_rr")] public bool? TpmsHardWarningRr { get; init; }
    [JsonPropertyName("tpms_last_seen_pressure_time_fl")] public long? TpmsLastSeenPressureTimeFl { get; init; }
    [JsonPropertyName("tpms_last_seen_pressure_time_fr")] public long? TpmsLastSeenPressureTimeFr { get; init; }
    [JsonPropertyName("tpms_last_seen_pressure_time_rl")] public long? TpmsLastSeenPressureTimeRl { get; init; }
    [JsonPropertyName("tpms_last_seen_pressure_time_rr")] public long? TpmsLastSeenPressureTimeRr { get; init; }
    [JsonPropertyName("tpms_pressure_fl")] public decimal? TpmsPressureFl { get; init; }
    [JsonPropertyName("tpms_pressure_fr")] public decimal? TpmsPressureFr { get; init; }
    [JsonPropertyName("tpms_pressure_rl")] public decimal? TpmsPressureRl { get; init; }
    [JsonPropertyName("tpms_pressure_rr")] public decimal? TpmsPressureRr { get; init; }
    [JsonPropertyName("tpms_rcp_front_value")] public decimal? TpmsRcpFrontValue { get; init; }
    [JsonPropertyName("tpms_rcp_rear_value")] public decimal? TpmsRcpRearValue { get; init; }
    [JsonPropertyName("tpms_soft_warning_fl")] public bool? TpmsSoftWarningFl { get; init; }
    [JsonPropertyName("tpms_soft_warning_fr")] public bool? TpmsSoftWarningFr { get; init; }
    [JsonPropertyName("tpms_soft_warning_rl")] public bool? TpmsSoftWarningRl { get; init; }
    [JsonPropertyName("tpms_soft_warning_rr")] public bool? TpmsSoftWarningRr { get; init; }
    [JsonPropertyName("valet_mode")] public bool? ValetMode { get; init; }
    [JsonPropertyName("valet_pin_needed")] public bool? ValetPinNeeded { get; init; }
    [JsonPropertyName("vehicle_name")] public string? VehicleName { get; init; }
    [JsonPropertyName("vehicle_self_test_progress")] public int? VehicleSelfTestProgress { get; init; }
    [JsonPropertyName("vehicle_self_test_requested")] public bool? VehicleSelfTestRequested { get; init; }
    [JsonPropertyName("webcam_available")] public bool? WebcamAvailable { get; init; }
    [JsonPropertyName("media_info")] public TeslaMediaInfoDto? MediaInfo { get; init; }
    [JsonPropertyName("media_state")] public TeslaMediaStateDto? MediaState { get; init; }
    [JsonPropertyName("software_update")] public TeslaSoftwareUpdateDto? SoftwareUpdate { get; init; }
    [JsonPropertyName("speed_limit_mode")] public TeslaSpeedLimitModeDto? SpeedLimitMode { get; init; }
}
