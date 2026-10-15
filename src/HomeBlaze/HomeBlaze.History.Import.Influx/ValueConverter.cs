using System.Globalization;

namespace HomeBlaze.History.Import.Influx;

/// <summary>
/// Converts a raw InfluxDB value (its CSV text) to the CLR value of the mapped v2 property.
/// </summary>
internal static class ValueConverter
{
    private const int DivisionDecimals = 6;

    /// <summary>
    /// Converts <paramref name="raw"/>, or returns false when the value cannot be represented in the
    /// target type (not a number, not finite, a fraction for an integer, or an unmapped string).
    /// </summary>
    public static bool TryConvert(MappingRow row, string raw, out object? value)
    {
        value = null;
        return row.Type switch
        {
            MappingValueType.Decimal => TryConvertDecimal(row, raw, out value),
            MappingValueType.Double => TryConvertDouble(row, raw, out value),
            MappingValueType.Long or MappingValueType.Int => TryConvertInteger(row, raw, out value),
            MappingValueType.Bool => TryConvertBool(raw, out value),
            _ => TryConvertString(row, raw, out value)
        };
    }

    private static bool TryConvertDecimal(MappingRow row, string raw, out object? value)
    {
        value = null;
        if (!TryParseFinite(raw, out var number))
        {
            return false;
        }

        // The cast keeps 15 significant digits, which drops the binary noise of a double
        // (26.700000000000003 becomes 26.7) the way the device's own decimal reading reads.
        var decimalValue = (decimal)number;
        value = row.Divisor == 1m
            ? decimalValue
            : Math.Round(decimalValue / row.Divisor, DivisionDecimals);
        return true;
    }

    private static bool TryConvertDouble(MappingRow row, string raw, out object? value)
    {
        value = null;
        if (!TryParseFinite(raw, out var number))
        {
            return false;
        }

        value = row.Divisor == 1m ? number : number / (double)row.Divisor;
        return true;
    }

    private static bool TryConvertInteger(MappingRow row, string raw, out object? value)
    {
        value = null;
        if (!TryParseFinite(raw, out var number))
        {
            return false;
        }

        var scaled = row.Divisor == 1m ? number : number / (double)row.Divisor;
        if (!double.IsInteger(scaled))
        {
            return false;
        }

        if (row.Type == MappingValueType.Int)
        {
            if (scaled is < int.MinValue or > int.MaxValue)
            {
                return false;
            }

            value = (int)scaled;
            return true;
        }

        if (scaled is < long.MinValue or >= long.MaxValue)
        {
            return false;
        }

        value = (long)scaled;
        return true;
    }

    private static bool TryConvertBool(string raw, out object? value)
    {
        var parsed = bool.TryParse(raw, out var booleanValue);
        value = parsed ? booleanValue : null;
        return parsed;
    }

    private static bool TryConvertString(MappingRow row, string raw, out object? value)
    {
        if (row.Values is null)
        {
            value = raw;
            return true;
        }

        if (row.Values.TryGetValue(raw, out var mapped))
        {
            value = mapped;
            return true;
        }

        value = row.FallbackValue;
        return row.FallbackValue is not null;
    }

    private static bool TryParseFinite(string raw, out double number) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
        double.IsFinite(number);
}
