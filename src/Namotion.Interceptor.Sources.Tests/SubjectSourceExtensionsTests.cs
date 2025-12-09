using Moq;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources.Tests;

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
        Assert.Equal(0, result.FailedChanges.Length);
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
            .ReturnsAsync(WriteResult.Success());

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
            .ReturnsAsync(WriteResult.Success());

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
                return new ValueTask<WriteResult>(WriteResult.Success());
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
    public async Task WriteChangesInBatchesAsync_MultipleBatches_StopsOnFirstFailure()
    {
        // Arrange
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
                    return new ValueTask<WriteResult>(WriteResult.Failure(new Exception("Batch 2 failed")));
                }
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Equal(2, callCount); // Should stop after second batch fails
        Assert.True(result.FailedChanges.Length > 0); // Remaining changes are marked as failed
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_BatchSizeZero_TreatedAsSingleBatch()
    {
        // Arrange
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock
            .Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WriteResult.Success());

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
            .ReturnsAsync(WriteResult.Success());

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
            .Returns((ReadOnlyMemory<SubjectPropertyChange> batchChanges, CancellationToken _) =>
            {
                // First batch returns partial failure (1 item failed)
                return new ValueTask<WriteResult>(WriteResult.PartialFailure(
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
            .ReturnsAsync(WriteResult.Success());

        var changes = CreateChanges(9); // 3 batches of 3

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.Null(result.Error);
        Assert.Equal(0, result.FailedChanges.Length);
        sourceMock.Verify(
            s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task WriteChangesInBatchesAsync_CompleteBatchFailure_ReturnsAllRemainingAsFailed()
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
                    return new ValueTask<WriteResult>(WriteResult.Failure(new Exception("Complete batch failure")));
                }
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        var changes = CreateChanges(5);

        // Act
        var result = await sourceMock.Object.WriteChangesInBatchesAsync(changes, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Error);
        Assert.Equal(1, callCount); // Should stop after first batch fails
        Assert.Equal(5, result.FailedChanges.Length); // All 5 changes should be marked as failed
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
