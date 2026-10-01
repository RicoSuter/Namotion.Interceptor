using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Testing;

/// <summary>
/// Records every entry logged through it, to assert on diagnostics. Safe to log to from several threads.
/// </summary>
public class RecordingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    /// <summary>
    /// Gets a snapshot of the recorded entries in logging order.
    /// </summary>
    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets the messages logged at <see cref="LogLevel.Warning"/>.
    /// </summary>
    public IReadOnlyList<string> Warnings => GetMessages(LogLevel.Warning);

    /// <summary>
    /// Gets the messages logged at <see cref="LogLevel.Error"/>.
    /// </summary>
    public IReadOnlyList<string> Errors => GetMessages(LogLevel.Error);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_entries)
        {
            _entries.Add((logLevel, message, exception));
        }
    }

    private string[] GetMessages(LogLevel level)
    {
        lock (_entries)
        {
            return _entries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToArray();
        }
    }
}

/// <summary>
/// A <see cref="RecordingLogger"/> for code that takes an <see cref="ILogger{TCategoryName}"/>.
/// </summary>
public sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>;

/// <summary>
/// Hands out the same <see cref="RecordingLogger"/> for every category, for code that creates its loggers through a provider.
/// </summary>
public sealed class RecordingLoggerProvider(RecordingLogger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => logger;

    public void Dispose()
    {
    }
}

/// <summary>
/// Hands out the same <see cref="RecordingLogger"/> for every category, for code that resolves an <see cref="ILoggerFactory"/>.
/// </summary>
public sealed class RecordingLoggerFactory(RecordingLogger logger) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => logger;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
