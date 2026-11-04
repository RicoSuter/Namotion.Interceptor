using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Records the warnings and errors that the server under test logs, for tests that check that a flow logs none.
/// </summary>
public sealed class ServerLogRecorder : ILoggerProvider
{
    private readonly ConcurrentQueue<ServerLogEntry> _entries = new();

    /// <summary>
    /// Gets the number of entries recorded so far, which a test takes before it acts to find the entries of its own
    /// flow afterwards with <see cref="GetEntriesSince"/>.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Gets the entries recorded after the first <paramref name="count"/> ones. Entries of other circuits that are
    /// still active, such as the one of an earlier test, can be among them.
    /// </summary>
    public IReadOnlyList<ServerLogEntry> GetEntriesSince(int count) => _entries.Skip(count).ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(ServerLogRecorder recorder, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                recorder._entries.Enqueue(new ServerLogEntry(categoryName, logLevel, formatter(state, exception), exception));
            }
        }
    }
}

/// <summary>
/// A warning or error that the server under test logged.
/// </summary>
public sealed record ServerLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
