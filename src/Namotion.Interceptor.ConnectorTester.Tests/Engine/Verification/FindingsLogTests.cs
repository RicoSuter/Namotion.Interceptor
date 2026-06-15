using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Verification;

public class FindingsLogTests
{
    [Fact]
    public void WhenSnapshotsConvergeQuicklyAndEqual_ThenNoFindingsWritten()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"findings-{Guid.NewGuid():N}.log");
        try
        {
            var log = new FindingsLog(path, cycleNumber: () => 1, chaosActive: () => false, NullLogger.Instance);
            const string snapshot = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";

            // Act
            log.AppendIfAny([("server", snapshot), ("client", snapshot)], TimeSpan.FromSeconds(1));

            // Assert
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WhenConvergenceSlowAndNoChaos_ThenSlowConvergenceFindingWritten()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"findings-{Guid.NewGuid():N}.log");
        try
        {
            var log = new FindingsLog(path, cycleNumber: () => 7, chaosActive: () => false, NullLogger.Instance);
            const string snapshot = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";

            // Act
            log.AppendIfAny([("server", snapshot), ("client", snapshot)], TimeSpan.FromSeconds(15));

            // Assert
            var contents = File.ReadAllText(path);
            Assert.Contains("Cycle 7:", contents);
            Assert.Contains("slow-convergence", contents);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WhenNullTimestampMismatch_ThenNullTimestampFindingWritten()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"findings-{Guid.NewGuid():N}.log");
        try
        {
            var log = new FindingsLog(path, cycleNumber: () => 3, chaosActive: () => true, NullLogger.Instance);
            const string serverSnap = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1,"timestamp":"2026-01-01T00:00:00+00:00"}}}}""";
            const string clientSnap = """{"root":"ROOT","subjects":{"ROOT":{"P":{"kind":"Value","value":1}}}}""";

            // Act
            log.AppendIfAny([("server", serverSnap), ("client", clientSnap)], TimeSpan.FromSeconds(2));

            // Assert
            var contents = File.ReadAllText(path);
            Assert.Contains("Cycle 3:", contents);
            Assert.Contains("null-timestamp", contents);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
