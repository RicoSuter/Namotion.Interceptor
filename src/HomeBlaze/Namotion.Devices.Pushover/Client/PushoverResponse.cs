using System.Text.Json.Serialization;

namespace Namotion.Devices.Pushover.Client;

/// <summary>
/// The response of every Pushover endpoint used here; each endpoint fills only its own members.
/// </summary>
internal sealed class PushoverResponse
{
    [JsonPropertyName("status")]
    public int Status { get; init; }

    [JsonPropertyName("request")]
    public string? Request { get; init; }

    [JsonPropertyName("errors")]
    public string[]? Errors { get; init; }

    /// <summary>
    /// "invalid" when the application token was rejected.
    /// </summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    /// <summary>
    /// "invalid" when the user or group key was rejected.
    /// </summary>
    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("receipt")]
    public string? Receipt { get; init; }

    [JsonPropertyName("devices")]
    public string[]? Devices { get; init; }

    [JsonPropertyName("acknowledged")]
    public int Acknowledged { get; init; }

    [JsonPropertyName("acknowledged_at")]
    public long AcknowledgedAt { get; init; }

    [JsonPropertyName("acknowledged_by_device")]
    public string? AcknowledgedByDevice { get; init; }

    [JsonPropertyName("expired")]
    public int Expired { get; init; }
}
