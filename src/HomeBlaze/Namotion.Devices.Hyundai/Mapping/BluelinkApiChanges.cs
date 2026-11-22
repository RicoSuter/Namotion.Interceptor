using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Hyundai.Mapping;

/// <summary>
/// Logs answers that suggest a change of the unofficial Bluelink API as warnings starting with "Bluelink API changed?",
/// once per field and value per process and at most 100 times. Thread-safe.
/// </summary>
internal static class BluelinkApiChanges
{
    // Bounds the memory when a field keeps sending new unknown values.
    private const int MaximumLogged = 100;

    private static readonly ConcurrentDictionary<(string Path, string? Value), byte> Logged = new();

    /// <summary>Logs a field that every status has, but this one does not.</summary>
    public static void LogMissing(ILogger logger, string path)
    {
        if (TryAdd(path, null))
        {
            logger.LogWarning("Bluelink API changed? The status has no {Path}.", path);
        }
    }

    /// <summary>Logs a value that is not a known code or not a number.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="path">The dotted path of the field.</param>
    /// <param name="value">The raw value; never a secret, a token, a PIN or a coordinate.</param>
    public static void LogUnknownValue(ILogger logger, string path, string value)
    {
        if (TryAdd(path, value))
        {
            logger.LogWarning("Bluelink API changed? {Path} has the unknown value {Value}.", path, value);
        }
    }

    private static bool TryAdd(string path, string? value) => Logged.Count < MaximumLogged && Logged.TryAdd((path, value), 0);

    /// <summary>Forgets what was logged; for tests only.</summary>
    internal static void Reset() => Logged.Clear();
}
