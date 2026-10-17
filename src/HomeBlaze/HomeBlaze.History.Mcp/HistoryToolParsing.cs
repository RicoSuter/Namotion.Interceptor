using System.Globalization;
using System.Security;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Mcp;

/// <summary>
/// Pure parsing and normalization for the <c>get_property_history</c> MCP tool. Kept separate so
/// the boundary parsing is unit-testable without a live MCP server or registry.
/// </summary>
public static class HistoryToolParsing
{
    /// <summary>All recognized aggregation identifiers (canonical PascalCase), from <see cref="HistoryAggregations.All"/>.</summary>
    public static readonly IReadOnlySet<string> AllAggregations =
        new HashSet<string>(HistoryAggregations.All, StringComparer.Ordinal);

    /// <summary>
    /// Normalizes a case-insensitive aggregation name to its canonical form, defaulting to
    /// <see cref="HistoryAggregations.Last"/> when omitted. Returns null when the name is unknown.
    /// </summary>
    public static string? NormalizeAggregation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return HistoryAggregations.Last;
        }

        foreach (var known in AllAggregations)
        {
            if (string.Equals(known, raw, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a bucket size: a number with an s/m/h/d (or ms) unit suffix (for example "5m"),
    /// or a TimeSpan literal ("00:05:00"). Null/blank means a raw (unbucketed) query. The result is
    /// always positive. Throws <see cref="FormatException"/> for an unrecognized or non-positive value.
    /// A bare number ("5") is rejected so it is not silently read as a count of days.
    /// </summary>
    public static TimeSpan? ParseBucket(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var splitIndex = 0;
        while (splitIndex < text.Length && (char.IsDigit(text[splitIndex]) || text[splitIndex] is '.' or '+' or '-'))
        {
            splitIndex++;
        }

        var unit = text[splitIndex..];
        if (splitIndex > 0 && unit.Length > 0 && unit.All(char.IsLetter)
            && double.TryParse(text[..splitIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            // double.TryParse succeeds on an overflowing literal by returning infinity, and the
            // TimeSpan factories then throw OverflowException, which the caller does not catch.
            if (value <= 0 || !double.IsFinite(value))
            {
                throw new FormatException("Bucket size must be positive and finite.");
            }

            try
            {
                return unit.ToLowerInvariant() switch
                {
                    "ms" => TimeSpan.FromMilliseconds(value),
                    "s" => TimeSpan.FromSeconds(value),
                    "m" => TimeSpan.FromMinutes(value),
                    "h" => TimeSpan.FromHours(value),
                    "d" => TimeSpan.FromDays(value),
                    _ => throw new FormatException($"Unknown bucket unit '{unit}'.")
                };
            }
            catch (OverflowException)
            {
                throw new FormatException($"Bucket size '{text}' is out of range.");
            }
        }

        if (text.Contains(':'))
        {
            TimeSpan span;
            try
            {
                span = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                throw new FormatException($"Bucket size '{text}' is out of range.");
            }

            if (span <= TimeSpan.Zero)
            {
                throw new FormatException("Bucket size must be positive.");
            }

            return span;
        }

        throw new FormatException(
            $"Could not parse bucket '{raw}'. Use a unit suffix like 5m, 30s, 1h, 7d, or a TimeSpan literal like 00:05:00.");
    }

    /// <summary>
    /// Parses a calendar bucket word (<c>day</c>, <c>week</c>, <c>month</c>, <c>year</c>, case-insensitive).
    /// Returns null for anything else, including fixed sizes such as <c>5m</c>.
    /// </summary>
    public static HistoryCalendarUnit? ParseCalendarUnit(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "day" => HistoryCalendarUnit.Day,
            "week" => HistoryCalendarUnit.Week,
            "month" => HistoryCalendarUnit.Month,
            "year" => HistoryCalendarUnit.Year,
            _ => null
        };

    /// <summary>
    /// Resolves an IANA time zone id. Throws <see cref="FormatException"/> for an unknown id.
    /// </summary>
    public static TimeZoneInfo ParseTimeZone(string id)
    {
        // On Unix the id becomes a path under the zoneinfo directory, so only plain relative ids reach the lookup.
        var trimmed = id.Trim();
        if (!IsPlainTimeZoneId(trimmed))
        {
            throw new FormatException($"Unknown time zone '{id}'.");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or SecurityException)
        {
            throw new FormatException($"Unknown time zone '{id}'.", exception);
        }
    }

    /// <summary>
    /// Gets the id to report for a time zone: its IANA id where the platform knows one, else its own id.
    /// </summary>
    internal static string GetIanaId(TimeZoneInfo timeZone)
    {
        if (timeZone.HasIanaId)
        {
            return timeZone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var ianaId) ? ianaId : timeZone.Id;
    }

    private static bool IsPlainTimeZoneId(string id)
    {
        if (id.Length == 0 || id[0] == '/' || id.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '+' or '-' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses an ISO 8601 timestamp to a UTC instant. An explicit offset or <c>Z</c> is honored; a timestamp without
    /// one is local time in <paramref name="timeZone"/>, where a skipped local time resolves to the instant after the
    /// gap and a repeated one to the earlier instant. Throws <see cref="FormatException"/> for an invalid timestamp.
    /// </summary>
    public static DateTimeOffset ParseTimestamp(string raw, TimeZoneInfo timeZone)
    {
        try
        {
            // RoundtripKind leaves the kind Unspecified exactly when the text carries no offset and no Z.
            var parsed = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return parsed.Kind == DateTimeKind.Unspecified
                ? BucketAlignment.LocalTimeToInstant(parsed, timeZone)
                : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FormatException($"The timestamp '{raw}' is out of range.", exception);
        }
    }

    /// <summary>
    /// Maps a property's declared type to the response value-type hint (number / string / boolean / enum).
    /// Derived from the type itself, not the storage column, so bool and enum are distinguished from their columns.
    /// </summary>
    public static string ValueType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            return "enum";
        }

        if (underlying == typeof(bool))
        {
            return "boolean";
        }

        if (underlying == typeof(string))
        {
            return "string";
        }

        return "number";
    }
}
