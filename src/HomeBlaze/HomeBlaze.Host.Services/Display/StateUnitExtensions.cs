using System.Globalization;
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
    private static readonly (StateUnit Unit, string Suffix, decimal Factor)[][] UnitFamilies =
    [
        [(StateUnit.Watt, "W", 1m), (StateUnit.Kilowatt, "kW", 1000m)],
        [(StateUnit.WattHour, "Wh", 1m), (StateUnit.KilowattHour, "kWh", 1000m)],
        [(StateUnit.Millimeter, "mm", 1m), (StateUnit.Meter, "m", 1000m), (StateUnit.Kilometer, "km", 1_000_000m)],
        [(StateUnit.Milliampere, "mA", 1m), (StateUnit.Ampere, "A", 1000m)],
        [(StateUnit.Kilobyte, "kB", 1m), (default, "MB", 1000m), (default, "GB", 1_000_000m)],
        [(StateUnit.KilobytePerSecond, "kB/s", 1m), (default, "MB/s", 1000m)],
    ];

    private static readonly Dictionary<StateUnit, (int FamilyIndex, int UnitIndex)> UnitFamilyLookup = BuildLookup();

    private static Dictionary<StateUnit, (int FamilyIndex, int UnitIndex)> BuildLookup()
    {
        var lookup = new Dictionary<StateUnit, (int, int)>();
        for (var familyIndex = 0; familyIndex < UnitFamilies.Length; familyIndex++)
        {
            var family = UnitFamilies[familyIndex];
            for (var unitIndex = 0; unitIndex < family.Length; unitIndex++)
            {
                var entry = family[unitIndex];
                if (entry.Unit != default)
                {
                    lookup[entry.Unit] = (familyIndex, unitIndex);
                }
            }
        }

        return lookup;
    }

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
            var unit = stateMetadata.Unit;

            // Percent and Currency use special formatting, not auto-scaling
            if (unit == StateUnit.Percent)
            {
                return $"{(int)(Convert.ToDecimal(value) * 100m)}%";
            }

            if (unit == StateUnit.Currency)
            {
                return $"{value:C}";
            }

            if (unit == StateUnit.HexColor)
            {
                return value.ToString() ?? "";
            }

            // Try to convert to decimal for auto-scaling
            try
            {
                var decimalValue = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return FormatWithUnit(decimalValue, unit);
            }
            catch
            {
                // Fall through to default formatting for non-convertible types
            }
        }

        // Default formatting for common types
        return value switch
        {
            bool b => b ? "Yes" : "No",
            DateTime dt => dt.ToString("g"),
            DateTimeOffset dto => $"{dto.ToLocalTime().ToString("g")} {dto.ToLocalTime():zzz}",
            Enum e => e.ToString(),
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>
    /// Formats a decimal value with auto-scaling within its unit family.
    /// </summary>
    public static string FormatWithUnit(decimal value, StateUnit unit)
    {
        // Check if this unit belongs to a scalable family
        if (UnitFamilyLookup.TryGetValue(unit, out var lookup))
        {
            var family = UnitFamilies[lookup.FamilyIndex];
            var inputEntry = family[lookup.UnitIndex];

            // Convert to base unit (multiply by the input unit's factor)
            var baseValue = value * inputEntry.Factor;

            // Find the best display unit: largest unit where |baseValue / factor| >= 1
            var bestIndex = 0; // default to smallest
            for (var i = family.Length - 1; i >= 0; i--)
            {
                if (Math.Abs(baseValue / family[i].Factor) >= 1m)
                {
                    bestIndex = i;
                    break;
                }
            }

            var bestEntry = family[bestIndex];
            var displayValue = baseValue / bestEntry.Factor;

            return FormatDecimalValue(displayValue, bestEntry.Suffix);
        }

        // No family found — use static suffix
        var suffix = GetUnitSuffix(unit);
        if (suffix != null)
        {
            return FormatDecimalValue(value, suffix);
        }

        return unit == StateUnit.Default
            ? value.ToString(CultureInfo.InvariantCulture)
            : $"{value.ToString(CultureInfo.InvariantCulture)} {unit}";
    }

    private static string FormatDecimalValue(decimal value, string suffix)
    {
        // Round to 2 decimal places, strip trailing zeros
        var rounded = Math.Round(value, 2);
        var formatted = rounded.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{formatted} {suffix}";
    }

    /// <summary>
    /// Gets the unit suffix string for a given StateUnit.
    /// </summary>
    /// <returns>The unit suffix (e.g., "°C", "W") or null if no suffix applies.</returns>
    public static string? GetUnitSuffix(StateUnit unit) => unit switch
    {
        StateUnit.Percent => "%",
        StateUnit.DegreeCelsius => "°C",
        StateUnit.Degree => "°",
        StateUnit.Watt => "W",
        StateUnit.Kilowatt => "kW",
        StateUnit.WattHour => "Wh",
        StateUnit.Volt => "V",
        StateUnit.Ampere => "A",
        StateUnit.Hertz => "Hz",
        StateUnit.Lumen => "lm",
        StateUnit.Lux => "lx",
        StateUnit.Kilometer => "km",
        StateUnit.Meter => "m",
        StateUnit.Millimeter => "mm",
        StateUnit.MillimeterPerHour => "mm/h",
        StateUnit.Kilobyte => "kB",
        StateUnit.KilobytePerSecond => "kB/s",
        StateUnit.MegabitPerSecond => "Mbit/s",
        StateUnit.KilowattHour => "kWh",
        StateUnit.Milliampere => "mA",
        StateUnit.LiterPerHour => "l/h",
        StateUnit.MeterPerSecond => "m/s",
        StateUnit.Hectopascal => "hPa",
        StateUnit.UvIndex => "UV",
        StateUnit.HexColor => "hex",
        _ => null
    };

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalSeconds < 5
            ? $"{ts.TotalMilliseconds} ms"
            : ts.TotalHours >= 1
                ? $"{ts.TotalHours:F1} h"
                : ts.ToString(@"hh\:mm\:ss");
    }
}
