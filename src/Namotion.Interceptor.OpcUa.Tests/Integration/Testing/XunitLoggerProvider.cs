using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Logger provider that writes to xUnit test output with prefix and elapsed time.
/// </summary>
public sealed class XunitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;
    private readonly string _prefix;
    private readonly LogLevel _minLevel;
    private readonly Stopwatch _stopwatch;

    public XunitLoggerProvider(ITestOutputHelper output, string prefix, Stopwatch stopwatch, LogLevel minLevel = LogLevel.Debug)
    {
        _output = output;
        _prefix = prefix;
        _minLevel = minLevel;
        _stopwatch = stopwatch;
    }

    public ILogger CreateLogger(string categoryName) => new XunitLogger(_output, _prefix, categoryName, _minLevel, _stopwatch);

    public void Dispose() { }

    private sealed class XunitLogger : ILogger
    {
        private readonly ITestOutputHelper _output;
        private readonly string _prefix;
        private readonly string _categoryName;
        private readonly LogLevel _minLevel;
        private readonly Stopwatch _stopwatch;

        public XunitLogger(ITestOutputHelper output, string prefix, string categoryName, LogLevel minLevel, Stopwatch stopwatch)
        {
            _output = output;
            _prefix = prefix;
            _categoryName = categoryName;
            _minLevel = minLevel;
            _stopwatch = stopwatch;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            try
            {
                var elapsed = _stopwatch.Elapsed;
                var time = $"{elapsed.TotalSeconds:F1}s";
                var level = GetLevelPrefix(logLevel);
                var category = GetShortCategory(_categoryName);
                var message = formatter(state, exception);

                _output.WriteLine($"[{time}] [{_prefix}] [{level}] {category}: {message}");

                if (exception != null)
                {
                    _output.WriteLine($"[{time}] [{_prefix}] [EXC] {exception.GetType().Name}: {exception.Message}");
                }
            }
            catch (InvalidOperationException)
            {
                // Test already finished, ignore
            }
        }

        private static string GetShortCategory(string category)
        {
            if (category.StartsWith("Namotion.Interceptor.OpcUa.Client."))
                return category["Namotion.Interceptor.OpcUa.Client.".Length..];
            if (category.StartsWith("Namotion.Interceptor.OpcUa.Server."))
                return category["Namotion.Interceptor.OpcUa.Server.".Length..];
            if (category.StartsWith("Namotion.Interceptor.OpcUa."))
                return category["Namotion.Interceptor.OpcUa.".Length..];
            if (category.StartsWith("Namotion.Interceptor."))
                return category["Namotion.Interceptor.".Length..];
            if (category.StartsWith("Opc.Ua."))
                return "SDK." + category["Opc.Ua.".Length..];
            return category;
        }

        private static string GetLevelPrefix(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}

/// <summary>
/// Extension methods for adding xUnit logging.
/// </summary>
public static class XunitLoggingExtensions
{
    public static ILoggingBuilder AddXunit(this ILoggingBuilder builder, TestLogger logger, string prefix, LogLevel minLevel = LogLevel.Debug)
    {
        builder.AddProvider(new XunitLoggerProvider(logger.Output, prefix, logger.Stopwatch, minLevel));
        return builder;
    }
}
