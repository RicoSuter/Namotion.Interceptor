using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class ChargingSessionsResponse
{
    [JsonPropertyName("data")]
    public ChargingSessionData[]? Data { get; set; }
}

internal class ChargingSessionData
{
    [JsonPropertyName("attributes")]
    public ChargingSessionAttributes? Attributes { get; set; }
}

internal class ChargingSessionAttributes
{
    [JsonPropertyName("energy")]
    public decimal Energy { get; set; }
}
