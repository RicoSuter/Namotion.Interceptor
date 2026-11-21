using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Tesla.Mapping;

/// <summary>
/// Converts Tesla API values to the units and types of the subjects.
/// </summary>
internal static class TeslaValues
{
    private const decimal KilometersPerMile = 1.609344m;

    private static readonly ConcurrentDictionary<string, byte> LoggedUnknownValues = new();

    /// <summary>Returns null for the placeholders Tesla sends instead of a missing text.</summary>
    public static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "<invalid>" ? null : value.Trim();

    public static decimal? MilesToKilometers(decimal? miles) => miles * KilometersPerMile;

    public static decimal? MilesPerHourToKilometersPerHour(decimal? milesPerHour) => milesPerHour * KilometersPerMile;

    public static decimal? Percent(decimal? value) => value / 100m;

    public static decimal? KilowattHoursToWattHours(decimal? kilowattHours) => kilowattHours * 1000m;

    public static decimal? KilowattsToWatts(decimal? kilowatts) => kilowatts * 1000m;

    public static DateTimeOffset? FromUnixSeconds(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    public static DateTimeOffset? FromUnixMilliseconds(long? milliseconds) =>
        milliseconds is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value) : null;

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

        if (System.Enum.TryParse<TEnum>(text.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out var result) &&
            System.Enum.IsDefined(result))
        {
            return result;
        }

        if (LoggedUnknownValues.TryAdd(typeof(TEnum).Name + ":" + text, 0))
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
