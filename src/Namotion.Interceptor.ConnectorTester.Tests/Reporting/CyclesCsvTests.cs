using Xunit;
using Namotion.Interceptor.ConnectorTester.Reporting;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;

namespace Namotion.Interceptor.ConnectorTester.Tests.Reporting;

public class CyclesCsvTests
{
    [Fact]
    public void WhenHeaderWritten_ThenLineMatchesTodaysFormat()
    {
        // Arrange
        var path = Path.GetTempFileName();
        try
        {
            using var file = CyclesCsv.Create(path);

            // Act
            file.WriteHeader();
            file.Dispose();

            // Assert: byte-identical to the header that VerificationEngine.ExecuteAsync wrote previously.
            var expected =
                "               Timestamp,  Cycle, Result,              Profile,  MutateSeconds,  ConvergeSeconds, CycleSeconds,   ValueMutations,  StructuralMutations,  ChaosEvents,     HeapMB,  ProcessMB"
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
            using var file = CyclesCsv.Create(path);
            var row = new CycleCsvRow(
                Timestamp: new DateTimeOffset(2026, 5, 12, 10, 30, 45, 123, TimeSpan.Zero),
                Cycle: 7,
                Result: CycleResult.Pass,
                Profile: "no-chaos",
                MutateSeconds: 60.0,
                ConvergeSeconds: 4.5,
                CycleSeconds: 65.7,
                ValueMutations: 12345,
                StructuralMutations: 0,
                ChaosEvents: 0,
                HeapMb: 42.3,
                ProcessMb: 128.9);

            // Act
            file.WriteHeader();
            file.AppendRow(row);
            file.Dispose();

            // Assert: row width and format exactly match VerificationEngine.CompactHeapAndLogCycle today.
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal(
                "2026-05-12T10:30:45.123Z,      7,   Pass,             no-chaos,           60.0,              4.5,         65.7,            12345,                    0,            0,       42.3,      128.9",
                lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
