using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

/// <summary>
/// Console output of level and message only, so a terminal capture fits the video frame without wrapping.
/// </summary>
public sealed class ShortConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "short";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var level = logEntry.LogLevel switch
        {
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error or LogLevel.Critical => "fail",
            _ => "dbug"
        };
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        textWriter.WriteLine(logEntry.Exception is null ? $"{level}: {message}" : $"{level}: {message} {logEntry.Exception.Message}");
    }
}
