using System.Text.Json.Serialization;

namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverUserRequest
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("user")]
    public required string User { get; init; }
}
