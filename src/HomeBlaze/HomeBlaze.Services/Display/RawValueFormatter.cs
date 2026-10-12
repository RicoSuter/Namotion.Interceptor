using System.Globalization;

namespace HomeBlaze.Services.Display;

/// <summary>
/// Writes a value the same way on every system, for markup and for comparing it with text.
/// </summary>
public static class RawValueFormatter
{
    /// <summary>
    /// Gets the value with the invariant culture, a date as ISO 8601, and an empty text for null.
    /// </summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
