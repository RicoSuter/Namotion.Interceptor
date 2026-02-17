using System.Collections.Concurrent;

namespace Namotion.Interceptor.ConnectorTester.Logging;

/// <summary>
/// Logger provider that writes to both console and per-cycle log files.
/// The verification engine signals cycle boundaries via StartNewCycle/FinishCycle.
/// </summary>
public sealed class CycleLoggerProvider : ILoggerProvider
{
    private const int MaxPassingLogFiles = 50;

    private readonly ConcurrentDictionary<string, CycleLogger> _loggers = new();
    private readonly string _logDirectory;
    private readonly Lock _fileLock = new();

    private StreamWriter? _currentWriter;
    private string? _currentFilePath;
    private int _currentCycle;

    public CycleLoggerProvider(string logDirectory = "logs")
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public void StartNewCycle(int cycleNumber)
    {
        lock (_fileLock)
        {
            _currentWriter?.Flush();
            _currentWriter?.Dispose();

            _currentCycle = cycleNumber;
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
            _currentFilePath = Path.Combine(_logDirectory,
                $"cycle-{cycleNumber:D3}-pending-{timestamp}.log");

            _currentWriter = new StreamWriter(_currentFilePath, append: false)
            {
                AutoFlush = true
            };
        }
    }

    public void FinishCycle(int cycleNumber, bool passed)
    {
        lock (_fileLock)
        {
            if (_currentWriter == null || _currentFilePath == null || _currentCycle != cycleNumber)
                return;

            _currentWriter.Flush();
            _currentWriter.Dispose();
            _currentWriter = null;

            // Rename file with result
            var result = passed ? "pass" : "FAIL";
            var newPath = _currentFilePath.Replace("-pending-", $"-{result}-");

            try
            {
                File.Move(_currentFilePath, newPath);
            }
            catch
            {
                // Best-effort rename
            }

            _currentFilePath = null;
        }

        CleanupOldLogFiles();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new CycleLogger(name, this));

    public void Dispose()
    {
        lock (_fileLock)
        {
            _currentWriter?.Dispose();
        }
    }

    internal void WriteToFile(string message)
    {
        lock (_fileLock)
        {
            _currentWriter?.WriteLine(message);
        }
    }

    /// <summary>
    /// Deletes old passing log files, keeping only the most recent ones.
    /// FAIL logs are always preserved for investigation.
    /// </summary>
    private void CleanupOldLogFiles()
    {
        try
        {
            var passingLogs = Directory.GetFiles(_logDirectory, "cycle-*-pass-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(MaxPassingLogFiles)
                .ToList();

            foreach (var file in passingLogs)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private sealed class CycleLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly CycleLoggerProvider _provider;

        public CycleLogger(string categoryName, CycleLoggerProvider provider)
        {
            _categoryName = categoryName;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var level = logLevel switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERR ",
                LogLevel.Critical => "CRIT",
                _ => "INFO"
            };
            var message = $"[{timestamp}] [{level}] [{_categoryName}] {formatter(state, exception)}";

            if (exception != null)
            {
                message += Environment.NewLine + exception;
            }

            // Write to cycle log file (console is handled by the default console provider)
            _provider.WriteToFile(message);
        }
    }
}
