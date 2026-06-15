using Microsoft.Extensions.Logging;
using Namotion.Interceptor.ConnectorTester.Engine.Verification;
using Namotion.Interceptor.ConnectorTester.Logging;
using Xunit;

namespace Namotion.Interceptor.ConnectorTester.Tests.Logging;

public class CycleLoggerProviderTests
{
    // Mirrors the exact message WebSocketSubjectClientSource logs when the server and client
    // structural hashes diverge. Detection matches it as a substring, so the formatted hash
    // arguments are irrelevant.
    private const string ConnectorHashMismatchWarning =
        "Structural hash mismatch: server=ab12cd34, client=ef56ab78. Triggering reconnection.";

    [Fact]
    public void WhenHashMismatchWarningLoggedDuringCycle_ThenItIsReported()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"cycle-log-{Guid.NewGuid():N}");
        var provider = new CycleLoggerProvider(directory);
        try
        {
            var logger = provider.CreateLogger("WebSocketSubjectClientSource/client");

            // Act
            provider.StartCycle(1);
            logger.LogInformation("normal activity");
            logger.LogWarning(ConnectorHashMismatchWarning);

            var warnings = provider.GetHashMismatchWarnings();

            // Assert
            Assert.Single(warnings);
            Assert.Contains("Structural hash mismatch", warnings[0]);
        }
        finally
        {
            provider.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WhenNoHashMismatchWarningLoggedDuringCycle_ThenNoneAreReported()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"cycle-log-{Guid.NewGuid():N}");
        var provider = new CycleLoggerProvider(directory);
        try
        {
            var logger = provider.CreateLogger("WebSocketSubjectClientSource/client");

            // Act
            provider.StartCycle(1);
            logger.LogInformation("normal activity");
            logger.LogWarning("some unrelated warning");

            var warnings = provider.GetHashMismatchWarnings();

            // Assert
            Assert.Empty(warnings);
        }
        finally
        {
            provider.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WhenNoCycleStarted_ThenNoWarningsReported()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"cycle-log-{Guid.NewGuid():N}");
        var provider = new CycleLoggerProvider(directory);
        try
        {
            // Act
            var warnings = provider.GetHashMismatchWarnings();

            // Assert
            Assert.Empty(warnings);
        }
        finally
        {
            provider.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
