using Namotion.Interceptor.OpcUa.Client.Consistency;

namespace Namotion.Interceptor.OpcUa.Tests.Client.Consistency;

/// <summary>
/// Tests for ConsistencyReadMetrics thread-safe metrics collection.
/// </summary>
public class ConsistencyReadMetricsTests
{
    [Fact]
    public void InitialState_AllMetricsAreZero()
    {
        // Arrange & Act
        var metrics = new ConsistencyReadMetrics();

        // Assert
        Assert.Equal(0, metrics.Scheduled);
        Assert.Equal(0, metrics.Executed);
        Assert.Equal(0, metrics.Coalesced);
        Assert.Equal(0, metrics.Failed);
    }

    [Fact]
    public void RecordScheduled_IncrementsCounter()
    {
        // Arrange
        var metrics = new ConsistencyReadMetrics();

        // Act
        metrics.RecordScheduled();
        metrics.RecordScheduled();

        // Assert
        Assert.Equal(2, metrics.Scheduled);
    }

    [Fact]
    public void RecordExecuted_IncrementsCounterByAmount()
    {
        // Arrange
        var metrics = new ConsistencyReadMetrics();

        // Act
        metrics.RecordExecuted(3);
        metrics.RecordExecuted(2);

        // Assert
        Assert.Equal(5, metrics.Executed);
    }

    [Fact]
    public void RecordCoalesced_IncrementsCounter()
    {
        // Arrange
        var metrics = new ConsistencyReadMetrics();

        // Act
        metrics.RecordCoalesced();
        metrics.RecordCoalesced();
        metrics.RecordCoalesced();

        // Assert
        Assert.Equal(3, metrics.Coalesced);
    }

    [Fact]
    public void RecordFailed_IncrementsCounter()
    {
        // Arrange
        var metrics = new ConsistencyReadMetrics();

        // Act
        metrics.RecordFailed();

        // Assert
        Assert.Equal(1, metrics.Failed);
    }

    [Fact]
    public void ToString_ReturnsFormattedString()
    {
        // Arrange
        var metrics = new ConsistencyReadMetrics();
        metrics.RecordScheduled();
        metrics.RecordScheduled();
        metrics.RecordExecuted(1);
        metrics.RecordCoalesced();
        metrics.RecordFailed();

        // Act
        var result = metrics.ToString();

        // Assert
        Assert.Equal("Scheduled=2, Executed=1, Coalesced=1, Failed=1", result);
    }

    [Fact]
    public async Task ConcurrentOperations_IsThreadSafe()
    {
        // Arrange
        var metrics = new ConsistencyReadMetrics();
        const int operationsPerThread = 250;
        const int threadCount = 4;

        // Act - Each thread performs all operation types
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < operationsPerThread; i++)
                {
                    metrics.RecordScheduled();
                    metrics.RecordExecuted(1);
                    metrics.RecordCoalesced();
                    metrics.RecordFailed();
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - Each counter should have threadCount * operationsPerThread
        var expected = threadCount * operationsPerThread;
        Assert.Equal(expected, metrics.Scheduled);
        Assert.Equal(expected, metrics.Executed);
        Assert.Equal(expected, metrics.Coalesced);
        Assert.Equal(expected, metrics.Failed);
    }
}
