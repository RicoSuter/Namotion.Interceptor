using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaGuiSettingsDto
{
    [JsonPropertyName("gui_24_hour_time")] public bool? Gui24HourTime { get; init; }
    [JsonPropertyName("gui_charge_rate_units")] public string? GuiChargeRateUnits { get; init; }
    [JsonPropertyName("gui_distance_units")] public string? GuiDistanceUnits { get; init; }
    [JsonPropertyName("gui_range_display")] public string? GuiRangeDisplay { get; init; }
    [JsonPropertyName("gui_temperature_units")] public string? GuiTemperatureUnits { get; init; }
    [JsonPropertyName("gui_tirepressure_units")] public string? GuiTirePressureUnits { get; init; }
    [JsonPropertyName("show_range_units")] public bool? ShowRangeUnits { get; init; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
}
