namespace HomeBlaze.Abstractions.Common;

/// <summary>
/// Represents a geographic coordinate with latitude, longitude, and optional altitude. Latitude and longitude are WGS84 degrees.
/// </summary>
public readonly record struct GeoCoordinate(double Latitude, double Longitude, double? Altitude = null);
