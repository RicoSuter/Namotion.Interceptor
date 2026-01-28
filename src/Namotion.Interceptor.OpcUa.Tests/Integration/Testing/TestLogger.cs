using System.Diagnostics;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Shared logger for tests that provides consistent timestamps across all components.
/// </summary>
public sealed class TestLogger
{
    private readonly ITestOutputHelper _output;
    private readonly Stopwatch _stopwatch;

    public TestLogger(ITestOutputHelper output)
    {
        _output = output;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// Gets the underlying test output helper.
    /// </summary>
    public ITestOutputHelper Output => _output;

    /// <summary>
    /// Gets the shared stopwatch for consistent timing.
    /// </summary>
    public Stopwatch Stopwatch => _stopwatch;

    /// <summary>
    /// Logs a test message with timestamp.
    /// </summary>
    public void Log(string message)
    {
        var time = $"{_stopwatch.Elapsed.TotalSeconds:F1}s";
        try
        {
            _output.WriteLine($"[{time}] [Test] {message}");
        }
        catch (InvalidOperationException)
        {
            // Ignore - this happens when logging during fixture cleanup
            // when there's no active test context
        }
    }
}
