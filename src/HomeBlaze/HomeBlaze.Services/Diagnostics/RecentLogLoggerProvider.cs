using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Writes log entries at or above the minimum level of a <see cref="RecentLogStore"/> into it. Never throws into the
/// logging caller and never logs itself. Scopes are not kept.
/// </summary>
internal sealed class RecentLogLoggerProvider(RecentLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RecentLogLogger(categoryName, store);

    public void Dispose()
    {
    }

    private sealed class RecentLogLogger(string category, RecentLogStore store) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => store.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Checked before formatting, so entries below the minimum level allocate nothing.
            if (!store.IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                store.Add(category, logLevel, eventId, formatter(state, exception), exception);
            }
            catch
            {
                // A failing formatter or exception message must not break the code that logs.
            }
        }
    }
}
