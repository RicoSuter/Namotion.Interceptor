using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaMediaStateDto
{
    [JsonPropertyName("remote_control_enabled")] public bool? RemoteControlEnabled { get; init; }
}
