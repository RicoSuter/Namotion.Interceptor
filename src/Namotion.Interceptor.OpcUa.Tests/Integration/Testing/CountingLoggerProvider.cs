using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Counts the log entries whose formatted message contains a fragment, so a test can assert on how
/// often a component logged rather than only on what it did.
/// </summary>
internal sealed class CountingLoggerProvider : ILoggerProvider
{
    private readonly string _messageFragment;
    private int _count;

    public CountingLoggerProvider(string messageFragment)
    {
        _messageFragment = messageFragment;
    }

    public int Count => Volatile.Read(ref _count);

    public ILogger CreateLogger(string categoryName) => new CountingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CountingLogger : ILogger
    {
        private readonly CountingLoggerProvider _owner;

        public CountingLogger(CountingLoggerProvider owner)
        {
            _owner = owner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(_owner._messageFragment, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _owner._count);
            }
        }
    }
}
