using System.Collections;
using System.Globalization;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Registry.Abstractions;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Services.Display;

/// <summary>
/// Extension methods for formatting property values with unit support.
/// </summary>
public static class StateUnitExtensions
{
    private static readonly (StateUnit Unit, string Suffix, decimal Factor)[][] UnitFamilies =
    [
        [(StateUnit.Watt, "W", 1m), (StateUnit.Kilowatt, "kW", 1000m)],
        [(StateUnit.VoltAmpere, "VA", 1m), (default, "kVA", 1000m)],
        [(StateUnit.VoltAmpereReactive, "var", 1m), (default, "kvar", 1000m)],
        [(StateUnit.VoltAmpereHour, "VAh", 1m), (default, "kVAh", 1000m)],
        [(StateUnit.VoltAmpereReactiveHour, "varh", 1m), (default, "kvarh", 1000m)],
        [(StateUnit.AmpereHour, "Ah", 1m), (default, "kAh", 1000m)],
        [(StateUnit.WattHour, "Wh", 1m), (StateUnit.KilowattHour, "kWh", 1000m)],
        [(StateUnit.Millimeter, "mm", 1m), (StateUnit.Meter, "m", 1000m), (StateUnit.Kilometer, "km", 1_000_000m)],
        [(StateUnit.Milliampere, "mA", 1m), (StateUnit.Ampere, "A", 1000m)],
        [(StateUnit.Byte, "B", 1m), (StateUnit.Kilobyte, "kB", 1000m), (default, "MB", 1_000_000m), (default, "GB", 1_000_000_000m)],
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
    /// Renders a property value with proper formatting including unit support. A value whose text can show a secret
    /// configuration value, such as a record with a secret property, and such an element of a list render as an empty text.
    /// </summary>
    public static string GetPropertyDisplayValue(
        this RegisteredSubjectProperty property, object? value, ITimeZoneDisplay? timeZone = null)
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

            // Currency and HexColor use special formatting, not auto-scaling
            if (unit == StateUnit.Currency)
            {
                return $"{value:C}";
            }

            if (unit == StateUnit.HexColor)
            {
                return value.ToString() ?? "";
            }

            // Try to convert to decimal for the percent form and for auto-scaling
            try
            {
                var decimalValue = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (unit == StateUnit.Percent)
                {
                    return FormatPercent(decimalValue);
                }

                return FormatWithUnit(decimalValue, unit);
            }
            catch
            {
                // Fall through to default formatting for a value that is no number a decimal can hold,
                // such as a text, NaN or an infinity
            }
        }

        return FormatDisplayValue(value, timeZone);
    }

    /// <summary>
    /// Formats a value as <see cref="GetPropertyDisplayValue"/> does for a property without a unit: Yes or No for a boolean,
    /// at most two decimals for a number, and a date in the time zone of the viewer, or of the server when it is null.
    /// </summary>
    public static string FormatDisplayValue(object value, ITimeZoneDisplay? timeZone = null)
    {
        return value switch
        {
            TimeSpan timeSpan => FormatTimeSpan(timeSpan),
            bool b => b ? "Yes" : "No",
            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
            float f => f.ToString("0.##", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
            DateTime dt => timeZone is not null
                ? timeZone.Format(dt)
                : dt.ToString("g"),
            DateTimeOffset dto => timeZone is not null
                ? timeZone.Format(dto)
                : $"{dto.ToLocalTime().ToString("g")} {dto.ToLocalTime():zzz}",
            Enum e => e.ToString(),
            IEnumerable<string> strings => string.Join("\n", strings),
            string text => text,
            // Binary payloads would otherwise render one line per byte.
            byte[] bytes => $"{bytes.Length} bytes",
            IEnumerable elements => JoinElements(elements),
            _ => FormatOther(value)
        };
    }

    // The property panel splits on '\n' to render one line per element. Subject collections and subject dictionaries
    // never arrive here because the panel lists them as child subjects instead; a dictionary of plain values does, and
    // renders one "[key, value]" line per entry.
    private static string JoinElements(IEnumerable elements) =>
        string.Join('\n', elements.Cast<object?>().Select(element => element is null ? "null" : FormatOther(element)));

    private static string FormatOther(object value) =>
        SecretValueTypes.HoldsSecret(value) ? string.Empty : value.ToString() ?? "";

    /// <summary>
    /// Formats a fraction as a percentage rounded to one decimal place, for example 0.756 as "75.6%".
    /// </summary>
    /// <exception cref="OverflowException">The fraction times 100 exceeds the range of <see cref="decimal"/>.</exception>
    public static string FormatPercent(decimal fraction)
    {
        var percent = Math.Round(fraction * 100m, 1);
        return $"{percent.ToString("0.##", CultureInfo.InvariantCulture)}%";
    }

    /// <summary>
    /// Formats a decimal value with auto-scaling within its unit family.
    /// </summary>
    public static string FormatWithUnit(decimal value, StateUnit unit)
    {
        if (TryGetScaledUnit(unit, value, out var suffix, out var factor))
        {
            return FormatDecimalValue(value * factor, suffix);
        }

        // No family found: use static suffix with space handling
        var unitInfo = GetUnitInfo(unit);
        if (unitInfo != null)
        {
            var rounded = Math.Round(value, 2);
            var formatted = rounded.ToString("0.##", CultureInfo.InvariantCulture);
            var separator = unitInfo.Value.NeedsSpace ? " " : "";
            return $"{formatted}{separator}{unitInfo.Value.Suffix}";
        }

        return unit == StateUnit.Default
            ? value.ToString(CultureInfo.InvariantCulture)
            : $"{value.ToString(CultureInfo.InvariantCulture)} {unit}";
    }

    /// <summary>
    /// Gets the unit of its family that <see cref="FormatWithUnit"/> shows a value of <paramref name="magnitude"/> in: the
    /// largest unit in which the value is at least 1, such as kWh for 1500 Wh. A value of 0 stays in <paramref name="unit"/>,
    /// as it is 0 in every unit and the one the property declares is the one a reader expects. Returns false for a unit of no family.
    /// </summary>
    /// <param name="unit">The unit of the value.</param>
    /// <param name="magnitude">The value in <paramref name="unit"/>; its sign is ignored.</param>
    /// <param name="suffix">The suffix of the unit to show the value in.</param>
    /// <param name="factor">The factor that converts a value in <paramref name="unit"/> to the unit to show it in.</param>
    public static bool TryGetScaledUnit(StateUnit unit, decimal magnitude, out string suffix, out decimal factor)
    {
        if (!UnitFamilyLookup.TryGetValue(unit, out var lookup))
        {
            suffix = string.Empty;
            factor = 1m;
            return false;
        }

        var family = UnitFamilies[lookup.FamilyIndex];
        var inputFactor = family[lookup.UnitIndex].Factor;

        if (magnitude == 0m)
        {
            suffix = family[lookup.UnitIndex].Suffix;
            factor = 1m;
            return true;
        }

        // Saturated rather than overflowing, which only picks the largest unit.
        var absolute = Math.Abs(magnitude);
        var baseValue = absolute > decimal.MaxValue / inputFactor ? decimal.MaxValue : absolute * inputFactor;

        // The largest unit in which the value is at least 1, or else the smallest.
        var bestIndex = 0;
        for (var index = family.Length - 1; index >= 0; index--)
        {
            if (baseValue / family[index].Factor >= 1m)
            {
                bestIndex = index;
                break;
            }
        }

        suffix = family[bestIndex].Suffix;
        factor = inputFactor / family[bestIndex].Factor;
        return true;
    }

    private static string FormatDecimalValue(decimal value, string suffix)
    {
        // Round to 2 decimal places, strip trailing zeros
        var rounded = Math.Round(value, 2);
        var formatted = rounded.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{formatted} {suffix}";
    }

    /// <summary>
    /// Gets the unit suffix and whether it needs a space separator.
    /// </summary>
    public static (string Suffix, bool NeedsSpace)? GetUnitInfo(StateUnit unit) => unit switch
    {
        StateUnit.Percent => ("%", false),
        StateUnit.DegreeCelsius => ("°C", false),
        StateUnit.Degree => ("°", false),
        StateUnit.Watt => ("W", true),
        StateUnit.Kilowatt => ("kW", true),
        StateUnit.VoltAmpere => ("VA", true),
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
        StateUnit.Byte => ("B", true),
        StateUnit.Kilobyte => ("kB", true),
        StateUnit.KilobytePerSecond => ("kB/s", true),
        StateUnit.MegabitPerSecond => ("Mbit/s", true),
        StateUnit.KilowattHour => ("kWh", true),
        StateUnit.Milliampere => ("mA", true),
        StateUnit.LiterPerHour => ("l/h", true),
        StateUnit.MeterPerSecond => ("m/s", true),
        StateUnit.Hectopascal => ("hPa", true),
        StateUnit.UvIndex => ("UV", true),
        StateUnit.HexColor => ("hex", true),
        StateUnit.Kelvin => ("K", true),
        StateUnit.Minute => ("min", true),
        StateUnit.Hour => ("h", true),
        StateUnit.VoltAmpereReactive => ("var", true),
        StateUnit.VoltAmpereHour => ("VAh", true),
        StateUnit.VoltAmpereReactiveHour => ("varh", true),
        StateUnit.AmpereHour => ("Ah", true),
        StateUnit.WattPerSquareMeter => ("W/m²", true),
        StateUnit.DecibelMilliwatt => ("dBm", true),
        _ => null
    };

    /// <summary>
    /// Gets the unit suffix string for a given StateUnit.
    /// </summary>
    public static string? GetUnitSuffix(StateUnit unit) => GetUnitInfo(unit)?.Suffix;

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalSeconds < 5
            ? $"{ts.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)} ms"
            : ts.TotalHours >= 1
                ? $"{ts.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h"
                : ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }
}
