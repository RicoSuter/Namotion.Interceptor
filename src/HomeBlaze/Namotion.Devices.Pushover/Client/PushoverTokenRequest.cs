using System.Text.Json.Serialization;

namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverTokenRequest
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }
}
