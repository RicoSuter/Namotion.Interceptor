namespace Namotion.Devices.Ecowitt.Models;

internal static class EcowittValueParser
{
    /// <summary>
    /// Parses a plain numeric value, stripping any non-numeric suffix.
    /// Returns null for null, empty, or disconnected sensor placeholders.
    /// </summary>
    public static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
            return null;

        // Strip any trailing non-numeric characters (unit suffixes)
        var span = value.AsSpan().Trim();
        var end = span.Length;
        while (end > 0 && !char.IsDigit(span[end - 1]) && span[end - 1] != '.')
            end--;

        if (end == 0)
            return null;

        if (decimal.TryParse(span[..end], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var result))
            return result;

        return null;
    }

    /// <summary>
    /// Parses a temperature value. The unit comes from a separate field in the API response.
    /// Converts °F to °C if needed.
    /// </summary>
    public static decimal? ParseTemperature(string? value, string? unit)
    {
        var parsed = ParseDecimal(value);
        if (parsed == null)
            return null;

        // Check if unit indicates Fahrenheit
        if (unit != null && (unit.Contains('F') || unit.Contains('℉')))
            return Math.Round((parsed.Value - 32m) * 5m / 9m, 1);

        return parsed;
    }

    /// <summary>
    /// Parses humidity value like "44%" and returns as 0..1 range.
    /// </summary>
    public static decimal? ParseHumidity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
            return null;

        var trimmed = value.Trim().TrimEnd('%').Trim();
        if (decimal.TryParse(trimmed, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var result))
            return result / 100m;

        return null;
    }

    /// <summary>
    /// Parses wind speed with unit detection. Converts to m/s.
    /// Supported formats: "1.8 m/s", "4.03 mph", "6.5 km/h", "3.5 knots"
    /// </summary>
    public static decimal? ParseWindSpeed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
            return null;

        var trimmed = value.Trim();

        if (trimmed.EndsWith("m/s", StringComparison.OrdinalIgnoreCase))
            return ParseDecimal(trimmed);

        if (trimmed.EndsWith("mph", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? Math.Round(parsed.Value * 0.44704m, 2) : null;
        }

        if (trimmed.EndsWith("km/h", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? Math.Round(parsed.Value / 3.6m, 2) : null;
        }

        if (trimmed.EndsWith("knots", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("kn", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? Math.Round(parsed.Value * 0.514444m, 2) : null;
        }

        // Plain number, assume m/s
        return ParseDecimal(trimmed);
    }

    /// <summary>
    /// Parses pressure with unit detection. Converts to hPa.
    /// Supported: "946.2 hPa", "27.94 inHg", "710.0 mmHg"
    /// </summary>
    public static decimal? ParsePressure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
            return null;

        var trimmed = value.Trim();

        if (trimmed.EndsWith("hPa", StringComparison.OrdinalIgnoreCase))
            return ParseDecimal(trimmed);

        if (trimmed.EndsWith("inHg", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? Math.Round(parsed.Value * 33.8639m, 1) : null;
        }

        if (trimmed.EndsWith("mmHg", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? Math.Round(parsed.Value * 1.33322m, 1) : null;
        }

        // Plain number, assume hPa
        return ParseDecimal(trimmed);
    }

    /// <summary>
    /// Parses rain amount. Converts to mm.
    /// Supported: "3.8 mm", "0.15 in"
    /// </summary>
    public static decimal? ParseRain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
            return null;

        var trimmed = value.Trim();

        if (trimmed.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
            return ParseDecimal(trimmed);

        if (trimmed.EndsWith("in", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? Math.Round(parsed.Value * 25.4m, 1) : null;
        }

        // Plain number, assume mm
        return ParseDecimal(trimmed);
    }

    /// <summary>
    /// Parses rain rate. Converts to mm/h.
    /// Supported: "0.0 mm/Hr", "0.0 in/Hr"
    /// </summary>
    public static decimal? ParseRainRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
            return null;

        var trimmed = value.Trim();

        if (trimmed.EndsWith("mm/Hr", StringComparison.OrdinalIgnoreCase))
            return ParseDecimal(trimmed);

        if (trimmed.EndsWith("in/Hr", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? Math.Round(parsed.Value * 25.4m, 1) : null;
        }

        // Plain number, assume mm/h
        return ParseDecimal(trimmed);
    }

    /// <summary>
    /// Normalizes a raw Ecowitt battery value to 0..1 range.
    /// Binary sensors (WH31): 0 = OK (1.0), 1 = Low (0.0).
    /// Level-scale sensors (WH34/WH35/WH40/WH51/WH55/WH57/WH41/WH45/WH90/WH85): 0-5 scale mapped to 0..1.
    /// </summary>
    public static decimal? NormalizeBatteryLevel(int? rawValue, bool isBinaryBattery)
    {
        if (rawValue == null || rawValue == 9)
            return null;

        if (isBinaryBattery)
            return rawValue == 0 ? 1.0m : 0.0m;

        // 0-5 voltage scale
        return Math.Clamp(rawValue.Value / 5.0m, 0m, 1m);
    }

    /// <summary>
    /// Parses illuminance. Converts to lux.
    /// Supported: "0.00 Klux" (Klux × 1000 = lux), "123.45 W/m²" (W/m² ÷ 0.0079 ≈ lux)
    /// </summary>
    public static decimal? ParseIlluminance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
            return null;

        var trimmed = value.Trim();

        if (trimmed.EndsWith("Klux", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? parsed.Value * 1000m : null;
        }

        if (trimmed.EndsWith("W/m²", StringComparison.Ordinal) ||
            trimmed.EndsWith("W/m2", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseDecimal(trimmed);
            return parsed != null ? Math.Round(parsed.Value / 0.0079m, 0) : null;
        }

        if (trimmed.EndsWith("lux", StringComparison.OrdinalIgnoreCase))
            return ParseDecimal(trimmed);

        // Plain number, assume lux
        return ParseDecimal(trimmed);
    }
}
