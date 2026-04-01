using System.Text.Json.Serialization;

namespace Namotion.Devices.Shelly.Model;

/// <summary>
/// Status of a cover:N component from Shelly.GetStatus.
/// </summary>
internal class ShellyCoverStatus
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("apower")]
    public decimal? ActivePower { get; set; }

    [JsonPropertyName("voltage")]
    public decimal? Voltage { get; set; }

    [JsonPropertyName("current")]
    public decimal? Current { get; set; }

    [JsonPropertyName("pf")]
    public decimal? PowerFactor { get; set; }

    [JsonPropertyName("freq")]
    public decimal? Frequency { get; set; }

    [JsonPropertyName("aenergy")]
    public ShellyEnergyData? ActiveEnergy { get; set; }

    [JsonPropertyName("temperature")]
    public ShellyTemperatureData? Temperature { get; set; }

    [JsonPropertyName("pos_control")]
    public bool? PositionControl { get; set; }

    [JsonPropertyName("last_direction")]
    public string? LastDirection { get; set; }

    [JsonPropertyName("current_pos")]
    public int? CurrentPosition { get; set; }
}
