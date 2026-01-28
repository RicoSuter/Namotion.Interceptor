using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions.Attributes;
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
        var stateAttribute = property.GetStateAttribute();
        if (stateAttribute != null && stateAttribute.Unit != StateUnit.Default)
        {
            return FormatWithUnit(value, stateAttribute.Unit);
        }

        // Default formatting for common types
        return value switch
        {
            bool b => b ? "Yes" : "No",
            DateTime dt => dt.ToString("g"),
            DateTimeOffset dto => dto.ToString("g"),
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

        var suffix = GetUnitSuffix(unit);
        if (suffix != null)
        {
            return $"{value} {suffix}";
        }

        return unit == StateUnit.Default
            ? value.ToString() ?? ""
            : $"{value} {unit}";
    }

    /// <summary>
    /// Gets the unit suffix string for a given StateUnit.
    /// </summary>
    /// <returns>The unit suffix (e.g., "°C", "W") or null if no suffix applies.</returns>
    public static string? GetUnitSuffix(StateUnit unit) => unit switch
    {
        StateUnit.Percent => "%",
        StateUnit.DegreeCelsius => "°C",
        StateUnit.Watt => "W",
        StateUnit.KiloWatt => "kW",
        StateUnit.WattHour => "Wh",
        StateUnit.Volt => "V",
        StateUnit.Ampere => "A",
        StateUnit.Hertz => "Hz",
        StateUnit.Lumen => "lm",
        StateUnit.Lux => "lx",
        StateUnit.Meter => "m",
        StateUnit.Millimeter => "mm",
        StateUnit.MillimeterPerHour => "mm/h",
        StateUnit.Kilobyte => "kB",
        StateUnit.KilobytePerSecond => "kB/s",
        StateUnit.MegabitsPerSecond => "Mbit/s",
        StateUnit.LiterPerHour => "l/h",
        StateUnit.HexColor => "hex",
        _ => null
    };

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
