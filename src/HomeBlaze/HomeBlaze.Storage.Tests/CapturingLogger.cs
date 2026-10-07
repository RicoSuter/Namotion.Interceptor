using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Tests;

internal class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _messages = new();

    public int CountMessages(string fragment, LogLevel? level = null) => _messages.Count(entry =>
        entry.Message.Contains(fragment, StringComparison.Ordinal) && (level is null || entry.Level == level));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _messages.Enqueue((logLevel, formatter(state, exception)));
}
