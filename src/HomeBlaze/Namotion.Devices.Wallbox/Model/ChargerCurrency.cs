using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class ChargerCurrency
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
