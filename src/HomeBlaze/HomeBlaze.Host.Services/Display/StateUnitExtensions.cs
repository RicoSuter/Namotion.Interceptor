using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// Extension methods for formatting property values with unit support.
/// </summary>
public static class StateUnitExtensions
{
    /// <summary>
    /// Renders a property value with proper formatting including unit support.
    /// </summary>
    public static string GetPropertyDisplayValue(this RegisteredSubjectProperty property, object? value)
    {
        if (value == null)
        {
            return "null";
        }

        // Handle TimeSpan specially (before unit formatting)
        if (value is TimeSpan ts)
        {
            return FormatTimeSpan(ts);
        }

        // Apply unit formatting if specified
        var stateMetadata = property.GetStateMetadata();
        if (stateMetadata != null && stateMetadata.Unit != StateUnit.Default)
        {
            return FormatWithUnit(value, stateMetadata.Unit);
        }

        // Default formatting for common types
        return value switch
        {
            bool b => b ? "Yes" : "No",
            DateTime dt => dt.ToString("g"),
            DateTimeOffset dto => dto.ToLocalTime().ToString("g zzz"),
            Enum e => e.ToString(),
            _ => value.ToString() ?? ""
        };
    }

    private static string FormatWithUnit(object value, StateUnit unit)
    {
        // Special handling for units that need value transformation
        if (unit == StateUnit.Percent)
        {
            return $"{(int)(Convert.ToDecimal(value) * 100m)}%";
        }

        if (unit == StateUnit.WattHour)
        {
            return FormatWattHour(value);
        }

        if (unit == StateUnit.Currency)
        {
            return $"{value:C}";
        }

        var unitInfo = GetUnitInfo(unit);
        if (unitInfo != null)
        {
            var separator = unitInfo.Value.NeedsSpace ? " " : "";
            return $"{value}{separator}{unitInfo.Value.Suffix}";
        }

        return unit == StateUnit.Default
            ? value.ToString() ?? ""
            : $"{value} {unit}";
    }

    /// <summary>
    /// Gets the unit suffix string for a given StateUnit.
    /// </summary>
    /// <returns>The unit suffix (e.g., "°C", "W") or null if no suffix applies.</returns>
    /// <summary>
    /// Gets the unit suffix and whether it needs a space separator.
    /// </summary>
    /// <returns>Tuple of (suffix, needsSpace), or null if no suffix applies.</returns>
    public static (string Suffix, bool NeedsSpace)? GetUnitInfo(StateUnit unit) => unit switch
    {
        StateUnit.Percent => ("%", false),
        StateUnit.DegreeCelsius => ("°C", false),
        StateUnit.Degree => ("°", false),
        StateUnit.Watt => ("W", true),
        StateUnit.KiloWatt => ("kW", true),
        StateUnit.WattHour => ("Wh", true),
        StateUnit.Volt => ("V", true),
        StateUnit.Ampere => ("A", true),
        StateUnit.Hertz => ("Hz", true),
        StateUnit.Lumen => ("lm", true),
        StateUnit.Lux => ("lx", true),
        StateUnit.Kilometer => ("km", true),
        StateUnit.Meter => ("m", true),
        StateUnit.Millimeter => ("mm", true),
        StateUnit.MillimeterPerHour => ("mm/h", true),
        StateUnit.Kilobyte => ("kB", true),
        StateUnit.KilobytePerSecond => ("kB/s", true),
        StateUnit.MegabitsPerSecond => ("Mbit/s", true),
        StateUnit.LiterPerHour => ("l/h", true),
        StateUnit.MetersPerSecond => ("m/s", true),
        StateUnit.Hectopascal => ("hPa", true),
        StateUnit.UvIndex => ("UV", true),
        StateUnit.HexColor => ("hex", true),
        _ => null
    };

    /// <summary>
    /// Gets the unit suffix string for a given StateUnit.
    /// </summary>
    /// <returns>The unit suffix (e.g., "°C", "W") or null if no suffix applies.</returns>
    public static string? GetUnitSuffix(StateUnit unit) => GetUnitInfo(unit)?.Suffix;

    private static string FormatWattHour(object value)
    {
        if (decimal.TryParse(value.ToString(), out var wh) && wh > 10000)
            return $"{Math.Round(wh / 1000, 3)} kWh";
        return $"{value} Wh";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalSeconds < 5
            ? $"{ts.TotalMilliseconds} ms"
            : ts.TotalHours >= 1
                ? $"{ts.TotalHours:F1} h"
                : ts.ToString(@"hh\:mm\:ss");
    }
}
