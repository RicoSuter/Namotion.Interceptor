using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class AuthResponse
{
    [JsonPropertyName("jwt")]
    public string? Jwt { get; set; }
}
