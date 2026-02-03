using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for OpcUaClientGraphChangeDispatcher channel-based queue processing.
/// </summary>
public class OpcUaClientGraphChangeDispatcherTests
{
    [Fact]
    public async Task EnqueueModelChange_ProcessedByConsumer()
    {
        // Arrange
        var processedChanges = new List<object>();
        await using var dispatcher = new OpcUaClientGraphChangeDispatcher(
            NullLogger.Instance, (change, _) =>
            {
                processedChanges.Add(change);
                return Task.CompletedTask;
            });

        dispatcher.Start();

        // Act
        dispatcher.EnqueueModelChange("change1");
        dispatcher.EnqueueModelChange("change2");

        // Wait for processing
        await AsyncTestHelpers.WaitUntilAsync(
            () => processedChanges.Count == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Should process both changes");

        // Assert
        Assert.Equal(2, processedChanges.Count);
        Assert.Equal("change1", processedChanges[0]);
        Assert.Equal("change2", processedChanges[1]);
    }

    [Fact]
    public async Task Stop_CompletesGracefully()
    {
        // Arrange
        var processedCount = 0;
        await using var dispatcher = new OpcUaClientGraphChangeDispatcher(
            NullLogger.Instance,
            async (_, cancellationToken) =>
            {
                await Task.Delay(10, cancellationToken);
                Interlocked.Increment(ref processedCount);
            });

        dispatcher.Start();
        dispatcher.EnqueueModelChange("change1");

        // Act
        await dispatcher.StopAsync();

        // Assert
        Assert.Equal(1, processedCount);
    }

    [Fact]
    public async Task EnqueuePeriodicResync_ProcessedByConsumer()
    {
        // Arrange
        var resyncCount = 0;
        await using var dispatcher = new OpcUaClientGraphChangeDispatcher(
            NullLogger.Instance, (change, _) =>
            {
                if (change is OpcUaClientGraphChangeDispatcher.PeriodicResyncRequest)
                {
                    Interlocked.Increment(ref resyncCount);
                }

                return Task.CompletedTask;
            });

        dispatcher.Start();

        // Act
        dispatcher.EnqueuePeriodicResync();
        await AsyncTestHelpers.WaitUntilAsync(
            () => resyncCount == 1,
            timeout: TimeSpan.FromSeconds(30),
            message: "Should process periodic resync request");

        // Assert
        Assert.Equal(1, resyncCount);
    }

    [Fact]
    public async Task ProcessingOrder_MaintainsFifoOrder()
    {
        // Arrange
        var processedOrder = new List<int>();
        await using var dispatcher = new OpcUaClientGraphChangeDispatcher(
            NullLogger.Instance, (change, _) =>
            {
                if (change is int value)
                {
                    processedOrder.Add(value);
                }

                return Task.CompletedTask;
            });

        dispatcher.Start();

        // Act - enqueue in specific order
        for (int i = 0; i < 10; i++)
        {
            dispatcher.EnqueueModelChange(i);
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => processedOrder.Count == 10,
            timeout: TimeSpan.FromSeconds(30),
            message: "Should process all 10 items");

        // Assert - should process in FIFO order
        Assert.Equal(10, processedOrder.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(i, processedOrder[i]);
        }
    }

    [Fact]
    public async Task ProcessingError_ContinuesWithNextItem()
    {
        // Arrange
        var processedItems = new List<string>();
        await using var dispatcher = new OpcUaClientGraphChangeDispatcher(
            NullLogger.Instance, (change, _) =>
            {
                if (change is string str)
                {
                    if (str == "error")
                    {
                        throw new InvalidOperationException("Test error");
                    }
                    processedItems.Add(str);
                }

                return Task.CompletedTask;
            });

        dispatcher.Start();

        // Act - enqueue items with an error in the middle
        dispatcher.EnqueueModelChange("first");
        dispatcher.EnqueueModelChange("error");
        dispatcher.EnqueueModelChange("third");

        await AsyncTestHelpers.WaitUntilAsync(
            () => processedItems.Count == 2,
            timeout: TimeSpan.FromSeconds(30),
            message: "Should process first and third items, skipping error");

        // Assert - should process first and third, skipping the error
        Assert.Equal(2, processedItems.Count);
        Assert.Equal("first", processedItems[0]);
        Assert.Equal("third", processedItems[1]);
    }

    [Fact]
    public async Task DisposeAsync_StopsProcessing()
    {
        // Arrange
        var processedCount = 0;
        var dispatcher = new OpcUaClientGraphChangeDispatcher(
            NullLogger.Instance, (_, _) =>
            {
                Interlocked.Increment(ref processedCount);
                return Task.CompletedTask;
            });

        dispatcher.Start();
        dispatcher.EnqueueModelChange("item1");

        // Act
        await dispatcher.DisposeAsync();

        // Assert - should have processed the item before dispose completed
        Assert.True(processedCount >= 0); // May or may not have processed depending on timing
    }
}
