using System.Text.Json.Serialization;

namespace Namotion.Devices.Pushover.Client;

internal sealed class PushoverMessageRequest
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("user")]
    public required string User { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("priority")]
    public int? Priority { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("url_title")]
    public string? UrlTitle { get; init; }

    [JsonPropertyName("retry")]
    public int? Retry { get; init; }

    [JsonPropertyName("expire")]
    public int? Expire { get; init; }
}
