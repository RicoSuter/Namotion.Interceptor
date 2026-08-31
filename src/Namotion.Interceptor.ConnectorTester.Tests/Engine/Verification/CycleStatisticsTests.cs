using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Engine.Chaos;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Reporting;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class CycleStatisticsTests
{
    [Fact]
    public void WhenRecordPassCalled_ThenCyclesCsvAppendsExactlyOneRow()
    {
        // Arrange
        var cyclesPath = Path.GetTempFileName();
        var chaosPath = Path.GetTempFileName();
        try
        {
            using var cyclesCsv = CyclesCsv.Create(cyclesPath);
            using var chaosCsv = ChaosEventsCsv.Create(chaosPath);
            cyclesCsv.WriteHeader();
            chaosCsv.WriteHeader();

            var statistics = new CycleStatistics(
                cyclesCsv, chaosCsv,
                new HeapSampler(),
                mutationEngines: new List<MutationEngine>(),
                chaosEngines: new List<ChaosEngine>(),
                mutatePhaseDuration: TimeSpan.FromSeconds(60),
                NullLogger.Instance);

            // Act
            statistics.RecordPass(cycleNumber: 1, cycleDuration: TimeSpan.FromSeconds(65), convergeDuration: TimeSpan.FromSeconds(5), profileName: "no-chaos");

            // Close writers so Windows lets us read the file.
            cyclesCsv.Dispose();
            chaosCsv.Dispose();

            // Assert: header line + 1 data line.
            Assert.Equal(2, File.ReadAllLines(cyclesPath).Length);
        }
        finally
        {
            File.Delete(cyclesPath);
            File.Delete(chaosPath);
        }
    }
}
