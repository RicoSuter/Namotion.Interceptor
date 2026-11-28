using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Testing;

namespace Namotion.ZWave.Tests.Testing;

/// <summary>
/// Records every entry, and throws after recording one at or above <see cref="MinimumLevel"/>, as a broken logging provider does.
/// </summary>
internal sealed class ThrowingLogger(LogLevel minimumLevel) : ILogger
{
    private readonly RecordingLogger _entries = new();

    public LogLevel MinimumLevel => minimumLevel;

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.Entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _entries.Log(logLevel, eventId, state, exception, formatter);
        if (logLevel >= minimumLevel)
        {
            throw new InvalidOperationException("The logger failed.");
        }
    }
}
