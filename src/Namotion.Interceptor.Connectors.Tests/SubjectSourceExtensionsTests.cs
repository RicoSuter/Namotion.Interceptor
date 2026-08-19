using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Connectors.Monitoring;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceExtensionsTests
{
    [Fact]
    public async Task WriteChangesInBatchesAsync_EmptyChanges_ReturnsSuccess()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        var changes = ReadOnlyMemory<SubjectPropertyChange>.Empty;

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        Assert.Empty(result.FailedChanges);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_SingleBatchWhenCountLessThanBatchSize_DelegatesDirectly()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(5);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(3);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_SingleBatchWhenCountEqualsBatchSize_DelegatesDirectly()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(3);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(3);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_MultipleBatches_SplitsCorrectly()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var batchSizes = new List<int>();
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                batchSizes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        Assert.Equal(3, batchSizes.Count); // 2 + 2 + 1
        Assert.Equal(2, batchSizes[0]);
        Assert.Equal(2, batchSizes[1]);
        Assert.Equal(1, batchSizes[2]);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_MultipleBatches_ContinuesPastAFailingBatch()
    {
        // Arrange - 5 changes, batch size 2, the middle batch fails
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 2) // Second batch fails
                {
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new Exception("Batch 2 failed")));
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert - the failing batch is condemned, the batches around it are written
        Assert.NotNull(result.Error);
        Assert.Equal(3, callCount);
        Assert.Equal(new[] { "Property2", "Property3" }, result.FailedChanges.Select(change => change.Property.Name));
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_BatchSizeZero_TreatedAsSingleBatch()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(10);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_NegativeBatchSize_TreatedAsSingleBatch()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(-1);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(10);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_FirstBatchPartialFailure_ReturnsCorrectRemainingChanges()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var changes = CreateChanges(5);
        var failedChange = changes.Span[1]; // Second item in first batch fails

        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                // First batch returns partial failure (1 item failed)
                return new ValueTask<WriteResult>(WriteResult.Failure(
                    new[] { failedChange },
                    new Exception("Partial failure")));
            });

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.True(result.FailedChanges.Length >= 1); // At least the partial failure + remaining
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_AllBatchesSucceed_ReturnsSuccess()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(3);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success);

        var changes = CreateChanges(9); // 3 batches of 3

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        Assert.Empty(result.FailedChanges);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_CompleteBatchFailure_ReturnsOnlyThatBatchAsFailed()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> batchChanges, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1) // First batch completely fails
                {
                    return new ValueTask<WriteResult>(WriteResult.Failure(batchChanges, new Exception("Complete batch failure")));
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert - a batch that enumerates its whole content as failed is condemned whole, and the two
        // batches behind it, including the uneven tail one, are attempted and written
        Assert.NotNull(result.Error);
        Assert.Equal(3, callCount);
        Assert.Equal(2, result.FailedChanges.Length);
    }

    [Fact]
    public async Task WhenAMiddleBatchFailsWithoutEnumeratingItsFailures_ThenTheRemainingBatchesAreNotAttempted()
    {
        // Arrange: 5 changes, batch size 2. The second batch fails with an error but no failed changes,
        // which is how a call that never got an answer reports: a timeout, a dropped channel, a
        // faulted session.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                callCount++;
                return new ValueTask<WriteResult>(callCount == 2
                    ? WriteResult.Failure(
                        ReadOnlyMemory<SubjectPropertyChange>.Empty, new InvalidOperationException("Wholesale boom"))
                    : WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: the tail batch is never handed to a source that is not answering, and the failing
        // batch plus the unattempted remainder are reported unconfirmed. The first batch was confirmed
        // written before the failure, so it must not be condemned with them.
        Assert.Equal(2, callCount);
        Assert.NotNull(result.Error);
        Assert.Equal("Wholesale boom", result.Error!.Message);
        Assert.Equal(
            new[] { "Property2", "Property3", "Property4" },
            result.FailedChanges.Select(change => change.Property.Name));
    }

    [Fact]
    public async Task WhenSingleBatchFailsWithoutEnumeratedFailedChanges_ThenAllChangesAreReportedFailed()
    {
        // Arrange: the source fails wholesale (error, no enumerated failed changes) on the
        // single-batch fast path.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Failure(
                    ReadOnlyMemory<SubjectPropertyChange>.Empty, new InvalidOperationException("Wholesale boom"))));

        var changes = CreateChanges(3);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: the choke point normalizes the unattributed error into "the whole batch failed".
        Assert.NotNull(result.Error);
        Assert.Equal("Wholesale boom", result.Error!.Message);
        Assert.Equal(3, result.FailedChanges.Length);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal($"Property{i}", result.FailedChanges[i].Property.Name);
        }
    }

    [Fact]
    public async Task WhenSourceThrowsOnSecondBatch_ThenConfirmedFirstBatchIsNotReportedFailed()
    {
        // Arrange: 5 changes, batch size 2. The first batch [0,1] is confirmed written,
        // the second batch [2,3] throws.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 2)
                {
                    throw new InvalidOperationException("Batch 2 boom");
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: the confirmed first batch counts as written; failed = the throwing batch
        // (outcome unknown) plus the unprocessed remainder.
        Assert.NotNull(result.Error);
        Assert.Equal("Batch 2 boom", result.Error!.Message);
        Assert.False(result.IsFullySuccessful);
        Assert.Equal(3, result.FailedChanges.Length);
        var failedNames = result.FailedChanges.Select(change => change.Property.Name).ToArray();
        Assert.DoesNotContain("Property0", failedNames);
        Assert.DoesNotContain("Property1", failedNames);
        Assert.Contains("Property2", failedNames);
        Assert.Contains("Property3", failedNames);
        Assert.Contains("Property4", failedNames);
    }

    [Fact]
    public async Task WhenFirstItemOfBatchFails_ThenFailedChangesContainTheActualFailedChange()
    {
        // Arrange: 6 changes, batch size 3. The first batch [0,1,2] fails item 0 only;
        // items 1 and 2 are written, and the second batch [3,4,5] is written.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(3);

        var changes = CreateChanges(6);
        var failedChange = changes.Span[0];

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                callCount++;
                return new ValueTask<WriteResult>(callCount == 1
                    ? WriteResult.Failure(new[] { failedChange }, new InvalidOperationException("Item 0 failed"))
                    : WriteResult.Success);
            });

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: failed = the actually failed change, at the granularity the source reported it;
        // the written items 1 and 2 and the second batch must not be reported as failed.
        Assert.NotNull(result.Error);
        Assert.Equal(2, callCount);
        Assert.Equal(new[] { "Property0" }, result.FailedChanges.Select(change => change.Property.Name));
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_RegularSource_SerializesWrites()
    {
        // Arrange
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken ct) =>
            {
                var current = Interlocked.Increment(ref concurrentCalls);
                maxConcurrentCalls = Math.Max(maxConcurrentCalls, current);

                // Wait a bit to allow potential concurrent calls to overlap
                await Task.Delay(50, ct);

                Interlocked.Decrement(ref concurrentCalls);
                return WriteResult.Success;
            });

        var changes = CreateChanges(1);

        // Act - Start multiple concurrent writes
        var tasks = new[]
        {
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask()
        };

        await Task.WhenAll(tasks);

        // Assert - Regular source should serialize writes (max 1 concurrent)
        Assert.Equal(1, maxConcurrentCalls);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_ConcurrentSource_AllowsConcurrentWrites()
    {
        // Arrange
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var source = new ConcurrentTestSource(async () =>
        {
            var current = Interlocked.Increment(ref concurrentCalls);
            maxConcurrentCalls = Math.Max(maxConcurrentCalls, current);

            // Signal when all 3 calls have started
            if (current >= 3)
            {
                allStarted.TrySetResult();
            }

            // Wait for signal to continue
            await canContinue.Task;

            Interlocked.Decrement(ref concurrentCalls);
            return WriteResult.Success;
        });

        var changes = CreateChanges(1);

        // Act - Start multiple concurrent writes
        var tasks = new[]
        {
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask(),
            source.WriteChangesInBatchesAsync(changes, CancellationToken.None).AsTask()
        };

        // Wait for all calls to start (or timeout)
        var allStartedTask = allStarted.Task;
        var timeoutTask = Task.Delay(1000);
        await Task.WhenAny(allStartedTask, timeoutTask);

        // Allow all to complete
        canContinue.SetResult();
        await Task.WhenAll(tasks);

        // Assert - Concurrent source should allow concurrent writes (max 3)
        Assert.Equal(3, maxConcurrentCalls);
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_CancellationDuringSemaphoreWait_ReturnsFailure()
    {
        // Arrange
        var blockingSource = new BlockingTestSource();
        var changes = CreateChanges(1);

        // Start a blocking write
        var blockingTask = blockingSource.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Try to start another write with a cancelled token
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var result = await blockingSource.WriteChangesInBatchesAsync(changes, cts.Token);

        // Assert - Should return failure instead of throwing
        Assert.NotNull(result.Error);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Error);

        // Cleanup
        blockingSource.UnblockWrite();
        await blockingTask;
    }

    [Fact]
    public async Task WhenAnEarlyBatchFails_ThenLaterBatchesAreStillWrittenToTheSource()
    {
        // Arrange: 4 changes, batch size 2. The first batch fails wholesale, so everything queued
        // behind it depends on the loop attempting the batches after a failure.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var writtenBatches = new List<string[]>();
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
            {
                writtenBatches.Add(batch.ToArray().Select(change => change.Property.Name).ToArray());
                return new ValueTask<WriteResult>(writtenBatches.Count == 1
                    ? WriteResult.Failure(batch, new InvalidOperationException("Batch 1 refused"))
                    : WriteResult.Success);
            });

        var changes = CreateChanges(4);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: the source itself must have seen the second batch, not just the result accounting.
        Assert.Equal(2, writtenBatches.Count);
        Assert.Equal(new[] { "Property0", "Property1" }, writtenBatches[0]);
        Assert.Equal(new[] { "Property2", "Property3" }, writtenBatches[1]);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task WhenAnEarlyBatchFails_ThenALaterSucceedingBatchIsNotReportedFailed()
    {
        // Arrange: 4 changes, batch size 2. The first batch fails wholesale, the second is written.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
            {
                callCount++;
                return new ValueTask<WriteResult>(callCount == 1
                    ? WriteResult.Failure(batch, new InvalidOperationException("Batch 1 refused"))
                    : WriteResult.Success);
            });

        var changes = CreateChanges(4);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: a written change reported as failed makes a source transaction revert locally while
        // the source keeps the new value.
        Assert.NotNull(result.Error);
        Assert.Equal(2, result.FailedChanges.Length);
        var failedNames = result.FailedChanges.Select(change => change.Property.Name).ToArray();
        Assert.Contains("Property0", failedNames);
        Assert.Contains("Property1", failedNames);
        Assert.DoesNotContain("Property2", failedNames);
        Assert.DoesNotContain("Property3", failedNames);
    }

    [Fact]
    public async Task WhenSourceThrowsAfterAnEarlierBatchFailed_ThenRecordedFailuresAndTheRemainderAreReportedFailed()
    {
        // Arrange: 6 changes, batch size 2. The first batch refuses item 0 and writes item 1, the
        // second batch throws, so the third is never attempted.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var changes = CreateChanges(6);
        var refusedChange = changes.Span[0];

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new ValueTask<WriteResult>(WriteResult.Failure(
                        new[] { refusedChange }, new InvalidOperationException("Item 0 refused")));
                }

                throw new InvalidOperationException("Batch 2 boom");
            });

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: the recorded refusal plus the throwing batch plus the unattempted remainder. Property1
        // was written by the failing batch, so a prefix slice from the throw point would condemn it.
        Assert.Equal(2, callCount);
        Assert.NotNull(result.Error);

        // Both errors are reported, the first one first, so the throw stays diagnosable: an
        // AggregateException renders every inner exception with its stack, Exception.Data renders nothing.
        var aggregate = Assert.IsType<AggregateException>(result.Error);
        Assert.Equal(
            new[] { "Item 0 refused", "Batch 2 boom" },
            aggregate.InnerExceptions.Select(inner => inner.Message));

        Assert.Equal(5, result.FailedChanges.Length);
        var failedNames = result.FailedChanges.Select(change => change.Property.Name).ToArray();
        Assert.Contains("Property0", failedNames);
        Assert.DoesNotContain("Property1", failedNames);
        Assert.Contains("Property2", failedNames);
        Assert.Contains("Property3", failedNames);
        Assert.Contains("Property4", failedNames);
        Assert.Contains("Property5", failedNames);
    }

    [Fact]
    public async Task WhenALaterBatchFailsDifferentlyFromAnEarlierRefusal_ThenBothErrorsAreReported()
    {
        // Arrange: 6 changes, batch size 2. The first batch answers an enumerated refusal for one
        // node, the second is written, the third dies unenumerated the way a dropped session does.
        // Reporting only the first error would blame a node refusal for the condemned tail and hide
        // the disconnect, which is the error an operator can act on.
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(2);

        var changes = CreateChanges(6);
        var refusedChange = changes.Span[0];

        var callCount = 0;
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
            {
                callCount++;
                return new ValueTask<WriteResult>(callCount switch
                {
                    1 => WriteResult.Failure(new[] { refusedChange }, new InvalidOperationException("Item 0 refused")),
                    3 => WriteResult.CallFailed(new InvalidOperationException("Session died")),
                    _ => WriteResult.Success
                });
            });

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert: both errors are carried, in batch order, and the failed changes are the recorded
        // refusal plus the unanswered batch and the unattempted remainder, not the written ones.
        Assert.Equal(3, callCount);
        var aggregate = Assert.IsType<AggregateException>(result.Error);
        Assert.Equal(
            new[] { "Item 0 refused", "Session died" },
            aggregate.InnerExceptions.Select(inner => inner.Message));
        Assert.Equal(
            new[] { "Property0", "Property4", "Property5" },
            result.FailedChanges.Select(change => change.Property.Name));
    }

    [Fact]
    public async Task WhenABatchFailsWhileCancelled_ThenTheRemainingBatchesAreNotAttempted()
    {
        // Arrange: 6 changes, batch size 2, and a source that ignores the token, so only the loop can
        // stop. The first batch fails with cancellation already requested.
        var source = new FailingTestSource(writeBatchSize: 2);
        var changes = CreateChanges(6);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var result = await source.WriteChangesInBatchesAsync(changes, cancellation.Token);

        // Assert - nothing behind the failing batch is attempted, and an unattempted batch is unconfirmed
        Assert.Equal(1, source.CallCount);
        Assert.NotNull(result.Error);
        Assert.Equal(6, result.FailedChanges.Length);
    }

    [Fact]
    public async Task WhenOnePropertyIsQueuedTwiceAndItsBatchFails_ThenTheSourceNeverSettlesOnTheOlderValue()
    {
        // Arrange: one change per batch, so the two properties travel in separate batches. The queue
        // holds two writes for Temperature and one for Pressure; the first batch of the first flush
        // fails, every later write succeeds.
        var queue = new WriteRetryQueue(100, NullLogger.Instance, new QueueMetrics(nameof(SourceMetrics.OutboundRetries)));
        var temperature = CreateProperty("Temperature");
        var pressure = CreateProperty("Pressure");

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(1);

        var writes = new List<(string Property, int Value)>();
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> batch, CancellationToken _) =>
            {
                foreach (var change in batch.ToArray())
                {
                    writes.Add((change.Property.Name, change.GetNewValue<int>()));
                }

                return new ValueTask<WriteResult>(writes.Count == 1
                    ? WriteResult.Failure(batch, new InvalidOperationException("Transport hiccup"))
                    : WriteResult.Success);
            });

        // Act
        queue.Enqueue(new[]
        {
            CreateChange(temperature, 5, revision: 5),
            CreateChange(pressure, 60, revision: 6),
            CreateChange(temperature, 9, revision: 9)
        });
        var firstFlush = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);
        var secondFlush = await queue.FlushAsync(sourceMock.Object, CancellationToken.None);

        // Assert: the failing batch does not hold Pressure back to a later tick, and Temperature is
        // only ever written with the newer value, so a retry cannot leave the source on the older one.
        Assert.False(firstFlush);
        Assert.True(secondFlush);
        Assert.Equal(3, writes.Count);
        Assert.Equal(("Temperature", 9), writes[0]);
        Assert.Equal(("Pressure", 60), writes[1]);
        Assert.Equal(("Temperature", 9), writes[2]);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void WhenEveryBatchSucceeds_ThenTheBatchingLoopAllocatesNothing()
    {
        // Arrange: measured against a control that writes the same changes in a single batch, so the
        // async state machine a debug build allocates per call cancels out and only the loop's own
        // cost remains. The source hands back a shared result and allocates nothing per call.
        const int iterations = 200;
        var changes = CreateChanges(8);

        // Act
        var singleBatch = MeasureSuccessfulWrites(new SucceedingTestSource(writeBatchSize: 0), changes, iterations);
        var multiBatch = MeasureSuccessfulWrites(new SucceedingTestSource(writeBatchSize: 2), changes, iterations);

        // Assert - a failure accumulator allocated on the batching path would cost tens of bytes per flush
        Assert.True(multiBatch - singleBatch <= 16,
            $"The batching loop allocated {multiBatch - singleBatch} bytes per all-success flush " +
            $"({multiBatch} batched, {singleBatch} single batch).");
    }

    /// <summary>
    /// Independent measurement rounds taken per allocation reading, of which the lowest is reported.
    /// </summary>
    private const int MeasurementRounds = 5;

    /// <summary>
    /// Runs all-success writes and returns the bytes allocated per write, taken as the lowest of
    /// several rounds. Stray allocation only ever inflates a round, so the cheapest round is the one
    /// that ran clean, and any leftover one-time cost lands in the first round and is discarded. An
    /// allocation on the measured path is present in every round and so survives the minimum, which
    /// is what keeps the bound as sharp as a single sample.
    /// </summary>
    private static long MeasureSuccessfulWrites(ISubjectSource source, ReadOnlyMemory<SubjectPropertyChange> changes, int iterations)
    {
        for (var i = 0; i < 20; i++)
        {
            RunSynchronousWrite(source, changes);
        }

        var lowest = long.MaxValue;
        for (var round = 0; round < MeasurementRounds; round++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                RunSynchronousWrite(source, changes);
            }

            lowest = Math.Min(lowest, (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations);
        }

        return lowest;
    }

    private static void RunSynchronousWrite(ISubjectSource source, ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var write = source.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Everything completes synchronously here, which is what keeps the allocations on this thread.
        Assert.True(write.IsCompleted);
        Assert.Null(write.GetAwaiter().GetResult().Error);
    }

    private static PropertyReference CreateProperty(string name)
    {
        return new PropertyReference(new Mock<IInterceptorSubject>().Object, name);
    }

    private static SubjectPropertyChange CreateChange(PropertyReference property, int newValue, long revision)
    {
        return SubjectPropertyChange.Create(
            property,
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            newValue - 1,
            newValue,
            revision);
    }

    private static SubjectPropertyChange CreateChange(int id)
    {
        var subjectMock = new Mock<IInterceptorSubject>();
        return SubjectPropertyChange.Create(
            new PropertyReference(subjectMock.Object, $"Property{id}"),
            ChangeOrigin.Local,
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

    /// <summary>
    /// Shared ISubjectSource member implementation for the two hand-rolled test doubles below,
    /// which differ only in RootSubject and WriteChangesAsync. Neither exercises state transitions,
    /// so State is fixed at Synchronizing for the lifetime of the double.
    /// </summary>
    private abstract class StateTrackingTestSource : ISubjectSource
    {
        public IInterceptorSubject RootSubject => throw new NotSupportedException();
        public virtual int WriteBatchSize => 0;
        public abstract ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);
        public Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken) => Task.FromResult<Action?>(null);

        public SourceState State => SourceState.Synchronizing;

        public DateTimeOffset StateChangeTime { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? LastSynchronizedAt => null;

        public SourceDiagnostics Diagnostics { get; } = new(new SourceMetrics());

        ConnectorDiagnostics ISubjectConnector.Diagnostics => Diagnostics;

        public event EventHandler<SourceEvent>? StateChanged
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// Test source that implements ISupportsConcurrentWrites to opt-out of automatic synchronization.
    /// </summary>
    private sealed class ConcurrentTestSource(Func<Task<WriteResult>> writeCallback)
        : StateTrackingTestSource, ISupportsConcurrentWrites
    {
        public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
            => await writeCallback();
    }

    /// <summary>
    /// Writes everything successfully with a shared result, so a write allocates only what the
    /// batching loop does. Concurrent writes are allowed to keep the write lock out of the measurement.
    /// </summary>
    private sealed class SucceedingTestSource(int writeBatchSize)
        : StateTrackingTestSource, ISupportsConcurrentWrites
    {
        public override int WriteBatchSize => writeBatchSize;

        public override ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
            => new(WriteResult.Success);
    }

    /// <summary>
    /// Fails every write wholesale and counts the calls. Concurrent writes are allowed so that an
    /// already cancelled token reaches the batching loop instead of failing at the write lock.
    /// </summary>
    private sealed class FailingTestSource(int writeBatchSize)
        : StateTrackingTestSource, ISupportsConcurrentWrites
    {
        public int CallCount { get; private set; }

        public override int WriteBatchSize => writeBatchSize;

        public override ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            CallCount++;
            return new ValueTask<WriteResult>(WriteResult.Failure(changes, new InvalidOperationException("Refused")));
        }
    }

    /// <summary>
    /// Test source that blocks on write until explicitly unblocked.
    /// </summary>
    private sealed class BlockingTestSource : StateTrackingTestSource
    {
        private readonly TaskCompletionSource _writeStarted = new();
        private readonly TaskCompletionSource _canComplete = new();

        public override async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        {
            _writeStarted.TrySetResult();
            await _canComplete.Task;
            return WriteResult.Success;
        }

        public void UnblockWrite() => _canComplete.TrySetResult();
    }
}
