using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class ChargerEcoSmart
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    [JsonPropertyName("percentage")]
    public int Percentage { get; set; }
}
