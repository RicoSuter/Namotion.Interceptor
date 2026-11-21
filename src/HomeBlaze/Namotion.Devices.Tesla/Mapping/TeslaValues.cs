using System.Collections.Concurrent;
using HomeBlaze.Abstractions.Common;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Tesla.Mapping;

/// <summary>
/// Converts Tesla API values to the units and types of the subjects.
/// </summary>
internal static class TeslaValues
{
    private const decimal KilometersPerMile = 1.609344m;

    // Smaller than decimal.MaxValue (about 7.9E28), so the cast from float never overflows.
    private const float MaximumDecimalFloat = 7.9E28f;

    private static readonly ConcurrentDictionary<(Type EnumType, string Value), byte> LoggedUnknownValues = new();

    /// <summary>Returns null for the placeholders Tesla sends instead of a missing text.</summary>
    public static string? Text(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == "<invalid>" ? null : trimmed;
    }

    /// <summary>Converts a protobuf float, or returns null for NaN, infinity and values outside the range of decimal.</summary>
    public static decimal? Decimal(float value) =>
        float.IsFinite(value) && Math.Abs(value) < MaximumDecimalFloat ? (decimal)value : null;

    public static decimal? MilesToKilometers(decimal? miles) => miles * KilometersPerMile;

    public static decimal? MilesPerHourToKilometersPerHour(decimal? milesPerHour) => milesPerHour * KilometersPerMile;

    public static decimal? Percent(decimal? value) => value / 100m;

    public static decimal? KilowattHoursToWattHours(decimal? kilowattHours) => kilowattHours * 1000m;

    public static decimal? KilowattsToWatts(decimal? kilowatts) => kilowatts * 1000m;

    public static DateTimeOffset? FromUnixSeconds(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    public static DateTimeOffset? FromUnixMilliseconds(long? milliseconds) =>
        milliseconds is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value) : null;

    public static TimeSpan? Minutes(decimal? minutes) => minutes is >= 0 ? TimeSpan.FromMinutes((double)minutes.Value) : null;

    /// <returns>The coordinate, or null when the latitude or the longitude is missing.</returns>
    public static GeoCoordinate? Coordinate(decimal? latitude, decimal? longitude) =>
        latitude is { } latitudeValue && longitude is { } longitudeValue ? new GeoCoordinate((double)latitudeValue, (double)longitudeValue) : null;

    public static TimeSpan? MinutesAfterMidnight(int? minutes) => minutes is >= 0 ? TimeSpan.FromMinutes(minutes.Value) : null;

    /// <summary>
    /// Parses a Tesla text into an enum, ignoring case and underscores. A value Tesla added later becomes Unknown and is logged once.
    /// </summary>
    public static TEnum? Enum<TEnum>(string? value, ILogger logger) where TEnum : struct, System.Enum
    {
        var text = Text(value);
        if (text is null)
        {
            return null;
        }

        // Enum.TryParse maps a numeric text to the member with that number, which Tesla never sends on purpose.
        if (!char.IsAsciiDigit(text[0]) && text[0] is not ('-' or '+') &&
            System.Enum.TryParse<TEnum>(text.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var result) &&
            System.Enum.IsDefined(result))
        {
            return result;
        }

        if (LoggedUnknownValues.TryAdd((typeof(TEnum), text), 0))
        {
            logger.LogInformation("Unknown Tesla value {Value} for {EnumType}.", text, typeof(TEnum).Name);
        }

        return default(TEnum);
    }

    public static TeslaShiftState? ShiftState(string? value) => Text(value) switch
    {
        null => null,
        "P" => TeslaShiftState.Park,
        "R" => TeslaShiftState.Reverse,
        "N" => TeslaShiftState.Neutral,
        "D" => TeslaShiftState.Drive,
        _ => TeslaShiftState.Unknown
    };
}
