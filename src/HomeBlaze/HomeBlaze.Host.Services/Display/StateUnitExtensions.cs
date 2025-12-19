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
        return unit switch
        {
            StateUnit.Percent => $"{(int)(Convert.ToDecimal(value) * 100m)}%",
            StateUnit.DegreeCelsius => $"{value} °C",
            StateUnit.Watt => $"{value} W",
            StateUnit.KiloWatt => $"{value} kW",
            StateUnit.WattHour => FormatWattHour(value),
            StateUnit.Volt => $"{value} V",
            StateUnit.Ampere => $"{value} A",
            StateUnit.Hertz => $"{value} Hz",
            StateUnit.Lumen => $"{value} lm",
            StateUnit.Lux => $"{value} lx",
            StateUnit.Meter => $"{value} m",
            StateUnit.Millimeter => $"{value} mm",
            StateUnit.MillimeterPerHour => $"{value} mm/h",
            StateUnit.Kilobyte => $"{value} kB",
            StateUnit.KilobytePerSecond => $"{value} kB/s",
            StateUnit.MegabitsPerSecond => $"{value} Mbit/s",
            StateUnit.LiterPerHour => $"{value} l/h",
            StateUnit.Currency => $"{value:C}",
            StateUnit.Default => value.ToString() ?? "",
            _ => $"{value} {unit}"
        };
    }

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
