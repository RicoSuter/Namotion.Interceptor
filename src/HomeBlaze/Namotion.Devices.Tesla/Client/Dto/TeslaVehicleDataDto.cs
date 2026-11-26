using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaVehicleDataDto
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("vin")] public string? Vin { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("in_service")] public bool? InService { get; init; }
    [JsonPropertyName("vehicle_id")] public long? VehicleId { get; init; }
    [JsonPropertyName("charge_state")] public TeslaChargeStateDto? ChargeState { get; init; }
    [JsonPropertyName("climate_state")] public TeslaClimateStateDto? ClimateState { get; init; }
    [JsonPropertyName("drive_state")] public TeslaDriveStateDto? DriveState { get; init; }
    [JsonPropertyName("gui_settings")] public TeslaGuiSettingsDto? GuiSettings { get; init; }
    [JsonPropertyName("vehicle_config")] public TeslaVehicleConfigDto? VehicleConfig { get; init; }
    [JsonPropertyName("vehicle_state")] public TeslaVehicleStateDto? VehicleState { get; init; }
}
