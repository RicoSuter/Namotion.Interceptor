using System.Text.Json.Serialization;

namespace Namotion.Devices.Shelly.Model;

/// <summary>
/// Status of a temperature:N component from Shelly.GetStatus.
/// </summary>
internal class ShellyTemperatureStatus
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("tC")]
    public decimal? TemperatureCelsius { get; set; }
}
