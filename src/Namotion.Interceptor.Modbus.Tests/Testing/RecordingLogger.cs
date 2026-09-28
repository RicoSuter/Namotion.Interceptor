using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Modbus.Tests.Testing;

/// <summary>Captures warning messages logged through it, to assert on diagnostics.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            lock (Warnings)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
