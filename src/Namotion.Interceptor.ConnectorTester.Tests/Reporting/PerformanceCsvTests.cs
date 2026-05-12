using Xunit;
using Namotion.Interceptor.ConnectorTester.Reporting;

namespace Namotion.Interceptor.ConnectorTester.Tests.Reporting;

public class PerformanceCsvTests
{
    [Fact]
    public void WhenHeaderWritten_ThenLineMatchesTodaysFormat()
    {
        // Arrange
        var path = Path.GetTempFileName();
        try
        {
            using var file = PerformanceCsv.Create(path);

            // Act
            file.WriteHeader();
            file.Dispose();

            // Assert: matches PerformanceProfiler header today.
            var expected =
                "               Timestamp,  Participant,  Cycle,   Received/s, Received-Average,   Received-P50,   Received-P90,   Received-P95,   Received-P99,   Received-P999,   Received-Max,  Received-Processing,    Published,     Received,         CPU%,    ProcessMB,       HeapMB, AllocationMB/s"
                + Environment.NewLine;
            Assert.Equal(expected, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WhenRowWritten_ThenLineMatchesTodaysRowFormat()
    {
        // Arrange
        var path = Path.GetTempFileName();
        try
        {
            using var file = PerformanceCsv.Create(path);
            var row = new PerformanceCsvRow(
                Timestamp: new DateTimeOffset(2026, 5, 12, 10, 30, 45, 123, TimeSpan.Zero),
                Participant: "server",
                Cycle: 1,
                ReceivedPerSecond: 1234.0,
                ReceivedAverage: 5.5,
                ReceivedP50: 4.0,
                ReceivedP90: 9.0,
                ReceivedP95: 11.0,
                ReceivedP99: 18.0,
                ReceivedP999: 25.0,
                ReceivedMax: 50.0,
                ReceivedProcessing: 1.5,
                Published: 100,
                Received: 200,
                CpuPercent: 12.3,
                ProcessMb: 128.9,
                HeapMb: 42.3,
                AllocationMbPerSec: 7.65);

            // Act
            file.WriteHeader();
            file.AppendRow(row);
            file.Dispose();

            // Assert: line widths and formats from PerformanceProfiler today.
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal(
                "2026-05-12T10:30:45.123Z,       server,      1,         1234,              5.5,            4.0,            9.0,           11.0,           18.0,            25.0,           50.0,                  1.5,          100,          200,         12.3,        128.9,         42.3,           7.65",
                lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
