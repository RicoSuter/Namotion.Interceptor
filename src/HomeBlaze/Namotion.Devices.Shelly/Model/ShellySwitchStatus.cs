using System.Text.Json.Serialization;

namespace Namotion.Devices.Shelly.Model;

/// <summary>
/// Status of a switch:N component from Shelly.GetStatus.
/// </summary>
internal class ShellySwitchStatus
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("output")]
    public bool? Output { get; set; }

    [JsonPropertyName("apower")]
    public decimal? ActivePower { get; set; }

    [JsonPropertyName("voltage")]
    public decimal? Voltage { get; set; }

    [JsonPropertyName("current")]
    public decimal? Current { get; set; }

    [JsonPropertyName("aenergy")]
    public ShellyEnergyData? ActiveEnergy { get; set; }

    [JsonPropertyName("temperature")]
    public ShellyTemperatureData? Temperature { get; set; }
}

internal class ShellyEnergyData
{
    [JsonPropertyName("total")]
    public decimal Total { get; set; }
}

internal class ShellyTemperatureData
{
    [JsonPropertyName("tC")]
    public decimal? TemperatureCelsius { get; set; }
}
