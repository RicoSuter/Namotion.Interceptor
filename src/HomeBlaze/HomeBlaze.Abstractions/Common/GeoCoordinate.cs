namespace HomeBlaze.Abstractions.Common;

/// <summary>
/// Represents a geographic coordinate with latitude, longitude, and optional altitude.
/// </summary>
public readonly record struct GeoCoordinate(double Latitude, double Longitude, double? Altitude = null);
