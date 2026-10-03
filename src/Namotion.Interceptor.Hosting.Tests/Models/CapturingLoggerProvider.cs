using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Hosting.Tests.Models;

/// <summary>Records every log entry at every level, from every category, for a test to assert on.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue(new CapturedLogEntry(logLevel, exception));
    }
}

public sealed record CapturedLogEntry(LogLevel Level, Exception? Exception);
