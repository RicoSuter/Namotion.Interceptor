using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaTokenResponseDto
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("id_token")] public string? IdToken { get; init; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}
