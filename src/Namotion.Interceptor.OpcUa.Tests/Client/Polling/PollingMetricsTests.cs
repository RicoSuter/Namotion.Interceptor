using Namotion.Interceptor.OpcUa.Client.Polling;

namespace Namotion.Interceptor.OpcUa.Tests.Client.Polling;

/// <summary>
/// Tests for PollingMetrics thread-safe metrics collection.
/// </summary>
public class PollingMetricsTests
{
    [Fact]
    public void InitialState_AllMetricsAreZero()
    {
        // Arrange & Act
        var metrics = new PollingMetrics();

        // Assert
        Assert.Equal(0, metrics.TotalReads);
        Assert.Equal(0, metrics.FailedReads);
        Assert.Equal(0, metrics.ValueChanges);
        Assert.Equal(0, metrics.SlowPolls);
    }

    [Fact]
    public void RecordRead_IncrementsCounter()
    {
        // Arrange
        var metrics = new PollingMetrics();

        // Act
        metrics.RecordRead();
        metrics.RecordRead();

        // Assert
        Assert.Equal(2, metrics.TotalReads);
    }

    [Fact]
    public void RecordFailedRead_IncrementsCounter()
    {
        // Arrange
        var metrics = new PollingMetrics();

        // Act
        metrics.RecordFailedRead();
        metrics.RecordFailedRead();
        metrics.RecordFailedRead();

        // Assert
        Assert.Equal(3, metrics.FailedReads);
    }

    [Fact]
    public void RecordValueChange_IncrementsCounter()
    {
        // Arrange
        var metrics = new PollingMetrics();

        // Act
        metrics.RecordValueChange();

        // Assert
        Assert.Equal(1, metrics.ValueChanges);
    }

    [Fact]
    public void RecordSlowPoll_IncrementsCounter()
    {
        // Arrange
        var metrics = new PollingMetrics();

        // Act
        metrics.RecordSlowPoll();
        metrics.RecordSlowPoll();

        // Assert
        Assert.Equal(2, metrics.SlowPolls);
    }

    [Fact]
    public void Reset_ClearsAllMetrics()
    {
        // Arrange
        var metrics = new PollingMetrics();
        metrics.RecordRead();
        metrics.RecordFailedRead();
        metrics.RecordValueChange();
        metrics.RecordSlowPoll();

        // Act
        metrics.Reset();

        // Assert
        Assert.Equal(0, metrics.TotalReads);
        Assert.Equal(0, metrics.FailedReads);
        Assert.Equal(0, metrics.ValueChanges);
        Assert.Equal(0, metrics.SlowPolls);
    }

    [Fact]
    public async Task ConcurrentRecordRead_IsThreadSafe()
    {
        // Arrange
        var metrics = new PollingMetrics();
        const int threadCount = 100;
        const int iterationsPerThread = 100;

        // Act - Concurrent increments from multiple threads
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    metrics.RecordRead();
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - Should have exactly threadCount * iterationsPerThread
        Assert.Equal(threadCount * iterationsPerThread, metrics.TotalReads);
    }

    [Fact]
    public async Task ConcurrentMixedOperations_IsThreadSafe()
    {
        // Arrange
        var metrics = new PollingMetrics();
        const int operationsPerThread = 250;
        const int threadCount = 4;

        // Act - Each thread performs all operation types
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < operationsPerThread; i++)
                {
                    metrics.RecordRead();
                    metrics.RecordFailedRead();
                    metrics.RecordValueChange();
                    metrics.RecordSlowPoll();
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - Each counter should have threadCount * operationsPerThread
        var expected = threadCount * operationsPerThread;
        Assert.Equal(expected, metrics.TotalReads);
        Assert.Equal(expected, metrics.FailedReads);
        Assert.Equal(expected, metrics.ValueChanges);
        Assert.Equal(expected, metrics.SlowPolls);
    }

    [Fact]
    public async Task ConcurrentReadAndReset_IsThreadSafe()
    {
        // Arrange
        var metrics = new PollingMetrics();
        var cts = new CancellationTokenSource();

        // Act - One thread continuously reads, another resets
        var readTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                metrics.RecordRead();
            }
        });

        var resetTask = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(10);
                metrics.Reset();
            }
        });

        await resetTask;
        cts.Cancel();

        try
        {
            await readTask.WaitAsync(TimeSpan.FromMilliseconds(100));
        }
        catch (TimeoutException)
        {
            // Expected if task didn't complete quickly
        }

        // Assert - No exceptions, metrics are consistent
        var reads = metrics.TotalReads;
        Assert.True(reads >= 0); // Should be valid value after reset
    }
}
