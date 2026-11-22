using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Tesla.Tests.Testing;

internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<LogLevel> Levels => Entries.Select(entry => entry.Level);

    /// <summary>The warnings that report a change of the Tesla API.</summary>
    public IEnumerable<string> ApiChanges => Entries
        .Where(entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("Tesla API changed? ", StringComparison.Ordinal))
        .Select(entry => entry.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
