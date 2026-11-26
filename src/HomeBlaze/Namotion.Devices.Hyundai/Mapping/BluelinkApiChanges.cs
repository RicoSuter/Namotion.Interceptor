using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Hyundai.Mapping;

/// <summary>
/// Logs answers that suggest a change of the unofficial Bluelink API as warnings starting with "Bluelink API changed?",
/// at most 100 per process and only while warnings are enabled. A missing field and an unexpected value are logged once
/// per field, the latter with the first value, shortened to 40 characters, as a sample; an unknown code is logged once
/// per field and code. Thread-safe.
/// </summary>
internal static class BluelinkApiChanges
{
    // Bounds the memory should a field keep sending new unknown codes.
    private const int MaximumLogged = 100;
    private const int MaximumValueLength = 40;

    private const string Missing = "missing";
    private const string Unexpected = "unexpected";

    private static readonly ConcurrentDictionary<(string Path, string Problem), byte> Logged = new();

    /// <summary>Logs a field that every status has, but this one does not.</summary>
    public static void LogMissing(ILogger logger, string path)
    {
        if (logger.IsEnabled(LogLevel.Warning) && TryAdd(path, Missing))
        {
            logger.LogWarning("Bluelink API changed? The status has no {Path}.", path);
        }
    }

    /// <summary>Logs a value of a field that has an unexpected format, such as a text where a number belongs.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="path">The dotted path of the field.</param>
    /// <param name="value">The value; never a secret, a token, a PIN or a coordinate.</param>
    public static void LogUnexpectedValue(ILogger logger, string path, JsonElement value)
    {
        if (logger.IsEnabled(LogLevel.Warning) && TryAdd(path, Unexpected))
        {
            logger.LogWarning("Bluelink API changed? {Path} has the unexpected value {Value}.", path, Describe(value));
        }
    }

    /// <summary>Logs a code that the parser does not know.</summary>
    public static void LogUnknownCode(ILogger logger, string path, int code)
    {
        if (logger.IsEnabled(LogLevel.Warning) && TryAdd(path, code.ToString(CultureInfo.InvariantCulture)))
        {
            logger.LogWarning("Bluelink API changed? {Path} has the unknown value {Value}.", path, code);
        }
    }

    private static bool TryAdd(string path, string problem) => Logged.Count < MaximumLogged && Logged.TryAdd((path, problem), 0);

    /// <summary>Returns the text of a string or number, shortened, and only the kind of anything else, so that an object never puts a part of the response into the log.</summary>
    private static string Describe(JsonElement value)
    {
        var text = value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : value.ValueKind.ToString();
        return text.Length > MaximumValueLength ? text[..MaximumValueLength] : text;
    }

    /// <summary>Forgets what was logged; for tests only.</summary>
    internal static void Reset() => Logged.Clear();
}
