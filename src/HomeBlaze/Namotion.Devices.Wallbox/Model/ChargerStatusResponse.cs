using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class ChargerStatusResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status_id")]
    public int StatusId { get; set; }

    [JsonPropertyName("charging_power")]
    public decimal ChargingPowerInKw { get; set; }

    [JsonPropertyName("max_available_power")]
    public decimal MaxAvailablePower { get; set; }

    [JsonPropertyName("charging_speed")]
    public decimal ChargingCurrent { get; set; }

    [JsonPropertyName("added_range")]
    public decimal AddedRange { get; set; }

    [JsonPropertyName("added_energy")]
    public decimal AddedEnergy { get; set; }

    [JsonPropertyName("added_green_energy")]
    public decimal AddedGreenEnergy { get; set; }

    [JsonPropertyName("added_discharged_energy")]
    public decimal AddedDischargedEnergy { get; set; }

    [JsonPropertyName("added_grid_energy")]
    public decimal AddedGridEnergy { get; set; }

    [JsonPropertyName("charging_time")]
    public int ChargingTime { get; set; }

    [JsonPropertyName("finished")]
    public bool Finished { get; set; }

    [JsonPropertyName("cost")]
    public decimal Cost { get; set; }

    [JsonPropertyName("current_mode")]
    public int CurrentMode { get; set; }

    [JsonPropertyName("state_of_charge")]
    public int? StateOfCharge { get; set; }

    [JsonPropertyName("depot_price")]
    public decimal DepotPrice { get; set; }

    [JsonPropertyName("config_data")]
    public ChargerConfiguration? ConfigData { get; set; }

    // Status codes from Wallbox cloud API (status_id field).
    // See https://github.com/cliviu74/wallbox/blob/master/wallbox/statuses.py
    // See https://github.com/home-assistant/core/blob/dev/homeassistant/components/wallbox/const.py
    public WallboxChargerStatus Status => StatusId switch
    {
        0 => WallboxChargerStatus.Disconnected,
        14 or 15 => WallboxChargerStatus.Error,
        161 or 162 => WallboxChargerStatus.Ready,
        163 => WallboxChargerStatus.Disconnected,
        164 => WallboxChargerStatus.Waiting,
        165 => WallboxChargerStatus.Locked,
        166 => WallboxChargerStatus.Updating,
        177 or 179 => WallboxChargerStatus.Scheduled,
        178 or 182 => WallboxChargerStatus.Paused,
        180 or 181 => WallboxChargerStatus.WaitingForCarDemand,
        183 or 184 => WallboxChargerStatus.WaitingInQueueByPowerSharing,
        185 or 186 => WallboxChargerStatus.WaitingInQueueByPowerBoost,
        187 => WallboxChargerStatus.WaitingMidFailed,
        188 => WallboxChargerStatus.WaitingMidSafetyMarginExceeded,
        189 => WallboxChargerStatus.WaitingInQueueByEcoSmart,
        193 or 194 or 195 => WallboxChargerStatus.Charging,
        196 => WallboxChargerStatus.Discharging,
        209 => WallboxChargerStatus.Locked,
        210 => WallboxChargerStatus.LockedCarConnected,
        _ => WallboxChargerStatus.Unknown
    };
}
