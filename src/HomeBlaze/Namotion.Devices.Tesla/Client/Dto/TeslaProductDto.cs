using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaProductDto
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("vehicle_id")] public long? VehicleId { get; init; }
    [JsonPropertyName("vin")] public string? Vin { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("in_service")] public bool? InService { get; init; }
    [JsonPropertyName("command_signing")] public string? CommandSigning { get; init; }
    [JsonPropertyName("cached_data")] public string? CachedData { get; init; }
    [JsonPropertyName("device_type")] public string? DeviceType { get; init; }
    [JsonPropertyName("access_type")] public string? AccessType { get; init; }
}
