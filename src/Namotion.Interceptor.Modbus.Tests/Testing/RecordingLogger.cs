using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Modbus.Tests.Testing;

/// <summary>Captures warning and error messages logged through it, to assert on diagnostics.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly List<string> _warnings = [];
    private readonly List<string> _errors = [];

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

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
            {
                return _errors.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var messages = logLevel switch
        {
            LogLevel.Warning => _warnings,
            LogLevel.Error => _errors,
            _ => null
        };

        if (messages is not null)
        {
            lock (messages)
            {
                messages.Add(formatter(state, exception));
            }
        }
    }
}
