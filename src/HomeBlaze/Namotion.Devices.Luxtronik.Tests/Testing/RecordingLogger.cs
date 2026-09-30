using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

/// <summary>
/// Captures the warning messages logged through it, to assert on diagnostics.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings)
            {
                return _warnings.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            lock (_warnings)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
