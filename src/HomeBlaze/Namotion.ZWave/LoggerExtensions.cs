using Microsoft.Extensions.Logging;

namespace Namotion.ZWave;

/// <summary>
/// Logging that ignores an exception of the logger, so that a broken logging provider cannot change what happens on the
/// serial line or stop a loop.
/// </summary>
internal static class LoggerExtensions
{
    public static bool TryIsEnabled(this ILogger logger, LogLevel level)
    {
        try
        {
            return logger.IsEnabled(level);
        }
        catch (Exception)
        {
            // The logger itself failed; nothing is logged then.
            return false;
        }
    }

    public static void TryLog(this ILogger logger, LogLevel level, Exception? exception, string message, params object?[] arguments)
    {
        try
        {
            logger.Log(level, exception, message, arguments);
        }
        catch (Exception)
        {
            // The logger itself failed; there is nowhere left to report the entry to.
        }
    }
}
