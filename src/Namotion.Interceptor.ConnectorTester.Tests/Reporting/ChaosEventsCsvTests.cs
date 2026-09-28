using Xunit;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Reporting;

namespace Namotion.Interceptor.ConnectorTester.Tests.Reporting;

public class ChaosEventsCsvTests
{
    [Fact]
    public void WhenHeaderWritten_ThenLineMatchesTodaysFormat()
    {
        // Arrange
        var path = Path.GetTempFileName();
        try
        {
            using var file = ChaosEventsCsv.Create(path);

            // Act
            file.WriteHeader();
            file.Dispose();

            // Assert
            var expected =
                "               Timestamp,  Cycle,      Participant,    FaultType,  DurationSeconds"
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
            using var file = ChaosEventsCsv.Create(path);
            var row = new ChaosEventCsvRow(
                Timestamp: new DateTimeOffset(2026, 5, 12, 10, 30, 45, 123, TimeSpan.Zero),
                Cycle: 7,
                Participant: "client-a",
                FaultType: FaultType.Kill,
                DurationSeconds: 3.5);

            // Act
            file.WriteHeader();
            file.AppendRow(row);
            file.Dispose();

            // Assert
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal(
                "2026-05-12T10:30:45.123Z,      7,         client-a,         Kill,              3.5",
                lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
