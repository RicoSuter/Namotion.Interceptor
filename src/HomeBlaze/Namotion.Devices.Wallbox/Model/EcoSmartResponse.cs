using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class EcoSmartResponse
{
    [JsonPropertyName("data")]
    public EcoSmartData? Data { get; set; }
}

internal class EcoSmartData
{
    [JsonPropertyName("attributes")]
    public EcoSmartAttributes? Attributes { get; set; }
}

internal class EcoSmartAttributes
{
    [JsonPropertyName("enabled")]
    public int Enabled { get; set; }

    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    [JsonPropertyName("percentage")]
    public int Percentage { get; set; }
}
