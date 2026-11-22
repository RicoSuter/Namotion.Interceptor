using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaResponse<T>
{
    [JsonPropertyName("response")] public T? Response { get; init; }
}
