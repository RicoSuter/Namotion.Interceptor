using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>Keeps what was logged, so that a test can assert on it.</summary>
public sealed class RecordingLogger<TCategory> : ILogger<TCategory>
{
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
