using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaDriveStateDto
{
    [JsonPropertyName("active_route_destination")] public string? ActiveRouteDestination { get; init; }
    [JsonPropertyName("active_route_energy_at_arrival")] public int? ActiveRouteEnergyAtArrival { get; init; }
    [JsonPropertyName("active_route_latitude")] public decimal? ActiveRouteLatitude { get; init; }
    [JsonPropertyName("active_route_longitude")] public decimal? ActiveRouteLongitude { get; init; }
    [JsonPropertyName("active_route_miles_to_arrival")] public decimal? ActiveRouteMilesToArrival { get; init; }
    [JsonPropertyName("active_route_minutes_to_arrival")] public decimal? ActiveRouteMinutesToArrival { get; init; }
    [JsonPropertyName("active_route_traffic_minutes_delay")] public decimal? ActiveRouteTrafficMinutesDelay { get; init; }
    [JsonPropertyName("gps_as_of")] public long? GpsAsOf { get; init; }
    [JsonPropertyName("heading")] public int? Heading { get; init; }
    [JsonPropertyName("latitude")] public decimal? Latitude { get; init; }
    [JsonPropertyName("longitude")] public decimal? Longitude { get; init; }
    [JsonPropertyName("native_latitude")] public decimal? NativeLatitude { get; init; }
    [JsonPropertyName("native_longitude")] public decimal? NativeLongitude { get; init; }
    [JsonPropertyName("native_location_supported")] public int? NativeLocationSupported { get; init; }
    [JsonPropertyName("native_type")] public string? NativeType { get; init; }
    [JsonPropertyName("power")] public int? Power { get; init; }
    [JsonPropertyName("shift_state")] public string? ShiftState { get; init; }
    [JsonPropertyName("speed")] public decimal? Speed { get; init; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
}
