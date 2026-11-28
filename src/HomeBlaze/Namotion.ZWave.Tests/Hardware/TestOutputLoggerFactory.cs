using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Namotion.ZWave.Tests.Hardware;

/// <summary>
/// Writes every log entry, including the Trace entries with the raw serial bytes, to the test output.
/// </summary>
internal sealed class TestOutputLoggerFactory(ITestOutputHelper output) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new TestOutputLogger(output, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class TestOutputLogger(ITestOutputHelper output, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var category = categoryName[(categoryName.LastIndexOf('.') + 1)..];
            output.WriteLine($"{logLevel,-11} {category}: {formatter(state, exception)}{(exception is null ? "" : $" {exception}")}");
        }
    }
}
