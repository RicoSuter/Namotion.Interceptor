using Namotion.Interceptor.OpcUa.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class ReconnectionMetricsTests
{
    [Fact]
    public void WhenCreated_ThenAllCountersAreZero()
    {
        // Arrange & Act
        var metrics = new ReconnectionMetrics();

        // Assert
        Assert.Equal(0, metrics.TotalAttempts);
        Assert.Equal(0, metrics.Successful);
        Assert.Equal(0, metrics.Failed);
        Assert.Null(metrics.LastConnectedAt);
    }

    [Fact]
    public void WhenRecordAttemptStart_ThenTotalAttemptsIncrements()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();

        // Act
        metrics.RecordAttemptStart();
        metrics.RecordAttemptStart();

        // Assert
        Assert.Equal(2, metrics.TotalAttempts);
    }

    [Fact]
    public void WhenRecordSuccess_ThenSuccessfulIncrementsAndLastConnectedAtIsSet()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();
        var before = DateTimeOffset.UtcNow;

        // Act
        metrics.RecordSuccess();

        // Assert
        Assert.Equal(1, metrics.Successful);
        Assert.NotNull(metrics.LastConnectedAt);
        Assert.True(metrics.LastConnectedAt >= before);
    }

    [Fact]
    public void WhenRecordFailure_ThenFailedIncrements()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();

        // Act
        metrics.RecordFailure();
        metrics.RecordFailure();
        metrics.RecordFailure();

        // Assert
        Assert.Equal(3, metrics.Failed);
    }

    [Fact]
    public async Task WhenConcurrentAccess_ThenCountersAreCorrect()
    {
        // Arrange
        var metrics = new ReconnectionMetrics();
        const int threadCount = 10;
        const int opsPerThread = 100;

        // Act
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < opsPerThread; i++)
                {
                    metrics.RecordAttemptStart();
                    metrics.RecordSuccess();
                    metrics.RecordFailure();
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var expected = threadCount * opsPerThread;
        Assert.Equal(expected, metrics.TotalAttempts);
        Assert.Equal(expected, metrics.Successful);
        Assert.Equal(expected, metrics.Failed);
    }
}
