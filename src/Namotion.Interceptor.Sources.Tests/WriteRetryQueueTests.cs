using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Tests;

public class WriteRetryQueueTests
{
    [Fact]
    public async Task WhenEnqueueAndFlush_ThenChangesAreWritten()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        SubjectPropertyChange[]? writtenChanges = null;
        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                writtenChanges = changes.ToArray())
            .Returns(ValueTask.CompletedTask);

        // Act
        queue.Enqueue(CreateChanges(3));
        var result = await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.NotNull(writtenChanges);
        Assert.Equal(3, writtenChanges.Length);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public async Task WhenQueueIsEmpty_ThenFlushReturnsTrue()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        // Act
        var result = await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        connectorMock.Verify(
            c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenQueueIsFull_ThenOldestAreDropped()
    {
        // Arrange
        var queue = new WriteRetryQueue(5, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        SubjectPropertyChange[]? writtenChanges = null;
        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                writtenChanges = changes.ToArray())
            .Returns(ValueTask.CompletedTask);

        // Act
        queue.Enqueue(CreateChanges(3, startId: 0));  // Items 0, 1, 2
        queue.Enqueue(CreateChanges(4, startId: 10)); // Items 10, 11, 12, 13 -> should drop 0, 1

        // Assert
        Assert.Equal(5, queue.PendingWriteCount);

        // Flush and verify the actual items (2, 10, 11, 12, 13)
        await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        Assert.NotNull(writtenChanges);
        Assert.Equal(5, writtenChanges.Length);
        Assert.Equal(2, writtenChanges[0].GetOldValue<int>());   // Item 2
        Assert.Equal(10, writtenChanges[1].GetOldValue<int>());  // Item 10
        Assert.Equal(11, writtenChanges[2].GetOldValue<int>());  // Item 11
        Assert.Equal(12, writtenChanges[3].GetOldValue<int>());  // Item 12
        Assert.Equal(13, writtenChanges[4].GetOldValue<int>());  // Item 13
    }

    [Fact]
    public async Task WhenFlushFails_ThenChangesAreRequeued()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection failed"));

        // Act
        queue.Enqueue(CreateChanges(3));
        var result = await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(3, queue.PendingWriteCount); // Re-queued
    }

    [Fact]
    public async Task WhenFlushFailsAtCapacity_ThenRequeueDoesNotDropItems()
    {
        // Arrange
        var queue = new WriteRetryQueue(5, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection failed"));

        // Act
        queue.Enqueue(CreateChanges(5)); // Fill to capacity
        var result = await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        // Assert
        Assert.False(result);
        Assert.Equal(5, queue.PendingWriteCount); // All items re-queued, none dropped
    }

    [Fact]
    public void WhenMaxQueueSizeIsZero_ThenWritesAreDropped()
    {
        // Arrange
        var queue = new WriteRetryQueue(0, NullLogger.Instance);

        // Act
        queue.Enqueue(CreateChanges(5));

        // Assert
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenManyItems_ThenFlushProcessesInBatches()
    {
        // Arrange
        var queue = new WriteRetryQueue(2000, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        var totalWritten = 0;
        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                totalWritten += changes.Length)
            .Returns(ValueTask.CompletedTask);

        // Act - enqueue more than MaxBatchSize (1024)
        queue.Enqueue(CreateChanges(1500));
        var result = await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.Equal(1500, totalWritten);
        Assert.True(queue.IsEmpty);

        // Should have been called at least twice (1024 + 476)
        connectorMock.Verify(
            c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task WhenCancelled_ThenFlushReturnsFalse()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        queue.Enqueue(CreateChanges(3));

        // Act
        var result = await queue.FlushAsync(connectorMock.Object, cts.Token);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task WhenMultipleFlushes_ThenOnlyOneRunsAtATime()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        var callCount = 0;
        var tcs = new TaskCompletionSource();

        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken ct) =>
            {
                Interlocked.Increment(ref callCount);
                await tcs.Task; // Block until released
            });

        queue.Enqueue(CreateChanges(3));

        // Act - start two flushes
        var flush1 = queue.FlushAsync(connectorMock.Object, CancellationToken.None);
        var flush2 = queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        await Task.Delay(100); // Let them start

        // Assert - only one should be running
        Assert.Equal(1, callCount);

        tcs.SetResult(); // Release the blocked flush
        await flush1;
        await flush2;
    }

    [Fact]
    public async Task WhenConnectorBatchSizeSet_ThenWriteChangesInBatchesRespectsBatchSize()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        var batchSizes = new List<int>();
        connectorMock.Setup(c => c.WriteBatchSize).Returns(2);
        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                batchSizes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        // Act
        queue.Enqueue(CreateChanges(5));
        await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        // Assert - WriteChangesInBatchesAsync should split into batches of 2
        Assert.Equal(3, batchSizes.Count); // 2 + 2 + 1
        Assert.Equal(2, batchSizes[0]);
        Assert.Equal(2, batchSizes[1]);
        Assert.Equal(1, batchSizes[2]);
    }

    [Fact]
    public async Task WhenConcurrentEnqueues_ThenAllItemsAreQueued()
    {
        // Arrange
        var queue = new WriteRetryQueue(10000, NullLogger.Instance);
        var tasks = new List<Task>();

        // Act - enqueue from multiple threads
        for (var i = 0; i < 10; i++)
        {
            var batch = i;
            tasks.Add(Task.Run(() => queue.Enqueue(CreateChanges(100, startId: batch * 100))));
        }

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(1000, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenFlushSucceeds_ThenQueueIsEmpty()
    {
        // Arrange
        var queue = new WriteRetryQueue(100, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        // Act
        queue.Enqueue(CreateChanges(10));
        await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        // Assert - queue should be empty after successful flush
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.PendingWriteCount);
    }

    [Fact]
    public async Task WhenExactlyMaxBatchSizeItems_ThenAllItemsAreFlushed()
    {
        // Arrange
        var queue = new WriteRetryQueue(2000, NullLogger.Instance);
        var connectorMock = new Mock<ISubjectSource>();

        var totalWritten = 0;
        connectorMock
            .Setup(c => c.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
                totalWritten += changes.Length)
            .Returns(ValueTask.CompletedTask);

        // Act - enqueue exactly MaxBatchSize (1024)
        queue.Enqueue(CreateChanges(1024));
        await queue.FlushAsync(connectorMock.Object, CancellationToken.None);

        // Assert
        Assert.Equal(1024, totalWritten);
        Assert.True(queue.IsEmpty);
    }
    
    private static SubjectPropertyChange CreateChange(int id)
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        return SubjectPropertyChange.Create(
            new PropertyReference(subjectMock.Object, $"Property{id}"),
            null,
            DateTimeOffset.UtcNow,
            null,
            id,
            id + 1);
    }

    private static ReadOnlyMemory<SubjectPropertyChange> CreateChanges(int count, int startId = 0)
    {
        var changes = new SubjectPropertyChange[count];
        for (var i = 0; i < count; i++)
        {
            changes[i] = CreateChange(startId + i);
        }
        return changes;
    }
}
