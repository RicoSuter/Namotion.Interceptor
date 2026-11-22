using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaChargeStateDto
{
    [JsonPropertyName("battery_level")] public int? BatteryLevel { get; init; }
    [JsonPropertyName("battery_range")] public decimal? BatteryRange { get; init; }
    [JsonPropertyName("charge_amps")] public int? ChargeAmps { get; init; }
    [JsonPropertyName("charge_current_request")] public int? ChargeCurrentRequest { get; init; }
    [JsonPropertyName("charge_current_request_max")] public int? ChargeCurrentRequestMax { get; init; }
    [JsonPropertyName("charge_enable_request")] public bool? ChargeEnableRequest { get; init; }
    [JsonPropertyName("charge_energy_added")] public decimal? ChargeEnergyAdded { get; init; }
    [JsonPropertyName("charge_limit_soc")] public int? ChargeLimitSoc { get; init; }
    [JsonPropertyName("charge_limit_soc_max")] public int? ChargeLimitSocMax { get; init; }
    [JsonPropertyName("charge_limit_soc_min")] public int? ChargeLimitSocMin { get; init; }
    [JsonPropertyName("charge_limit_soc_std")] public int? ChargeLimitSocStd { get; init; }
    [JsonPropertyName("charge_miles_added_ideal")] public decimal? ChargeMilesAddedIdeal { get; init; }
    [JsonPropertyName("charge_miles_added_rated")] public decimal? ChargeMilesAddedRated { get; init; }
    [JsonPropertyName("charge_port_cold_weather_mode")] public bool? ChargePortColdWeatherMode { get; init; }
    [JsonPropertyName("charge_port_color")] public string? ChargePortColor { get; init; }
    [JsonPropertyName("charge_port_door_open")] public bool? ChargePortDoorOpen { get; init; }
    [JsonPropertyName("charge_port_latch")] public string? ChargePortLatch { get; init; }
    [JsonPropertyName("charge_rate")] public decimal? ChargeRate { get; init; }
    [JsonPropertyName("charger_actual_current")] public int? ChargerActualCurrent { get; init; }
    [JsonPropertyName("charger_phases")] public int? ChargerPhases { get; init; }
    [JsonPropertyName("charger_pilot_current")] public int? ChargerPilotCurrent { get; init; }
    [JsonPropertyName("charger_power")] public int? ChargerPower { get; init; }
    [JsonPropertyName("charger_voltage")] public int? ChargerVoltage { get; init; }
    [JsonPropertyName("charging_state")] public string? ChargingState { get; init; }
    [JsonPropertyName("conn_charge_cable")] public string? ConnChargeCable { get; init; }
    [JsonPropertyName("est_battery_range")] public decimal? EstBatteryRange { get; init; }
    [JsonPropertyName("fast_charger_brand")] public string? FastChargerBrand { get; init; }
    [JsonPropertyName("fast_charger_present")] public bool? FastChargerPresent { get; init; }
    [JsonPropertyName("fast_charger_type")] public string? FastChargerType { get; init; }
    [JsonPropertyName("ideal_battery_range")] public decimal? IdealBatteryRange { get; init; }
    [JsonPropertyName("max_range_charge_counter")] public int? MaxRangeChargeCounter { get; init; }
    [JsonPropertyName("minutes_to_full_charge")] public int? MinutesToFullCharge { get; init; }
    [JsonPropertyName("off_peak_charging_enabled")] public bool? OffPeakChargingEnabled { get; init; }
    [JsonPropertyName("off_peak_charging_times")] public string? OffPeakChargingTimes { get; init; }
    [JsonPropertyName("off_peak_hours_end_time")] public int? OffPeakHoursEndTime { get; init; }
    [JsonPropertyName("preconditioning_enabled")] public bool? PreconditioningEnabled { get; init; }
    [JsonPropertyName("preconditioning_times")] public string? PreconditioningTimes { get; init; }
    [JsonPropertyName("scheduled_charging_mode")] public string? ScheduledChargingMode { get; init; }
    [JsonPropertyName("scheduled_charging_pending")] public bool? ScheduledChargingPending { get; init; }
    [JsonPropertyName("scheduled_charging_start_time")] public long? ScheduledChargingStartTime { get; init; }
    [JsonPropertyName("scheduled_charging_start_time_app")] public int? ScheduledChargingStartTimeApp { get; init; }
    [JsonPropertyName("scheduled_charging_start_time_minutes")] public int? ScheduledChargingStartTimeMinutes { get; init; }
    [JsonPropertyName("scheduled_departure_time")] public long? ScheduledDepartureTime { get; init; }
    [JsonPropertyName("scheduled_departure_time_minutes")] public int? ScheduledDepartureTimeMinutes { get; init; }
    [JsonPropertyName("supercharger_session_trip_planner")] public bool? SuperchargerSessionTripPlanner { get; init; }
    [JsonPropertyName("time_to_full_charge")] public decimal? TimeToFullCharge { get; init; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
    [JsonPropertyName("trip_charging")] public bool? TripCharging { get; init; }
    [JsonPropertyName("usable_battery_level")] public int? UsableBatteryLevel { get; init; }
    [JsonPropertyName("user_charge_enable_request")] public bool? UserChargeEnableRequest { get; init; }
}
