using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for source integration: SetSource, TryGetSource, WriteChangesAsync, and multi-context scenarios.
/// </summary>
public class SubjectTransactionSourceTests : TransactionTestBase
{
    [Fact]
    public void SetSource_StoresSourceReference()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();

        // Act
        property.SetSource(sourceMock);

        // Assert
        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(sourceMock, retrievedSource);
    }

    [Fact]
    public void TryGetSource_WhenNoSourceSet_ReturnsFalse()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var result = property.TryGetSource(out var source);

        // Assert
        Assert.False(result);
        Assert.Null(source);
    }

    [Fact]
    public void SetSource_WhenCalledWithDifferentSource_ReturnsFalse()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source1Mock = Mock.Of<ISubjectSource>();
        var source2Mock = Mock.Of<ISubjectSource>();

        // Act
        var result1 = property.SetSource(source1Mock);
        var result2 = property.SetSource(source2Mock);

        // Assert
        Assert.True(result1);
        Assert.False(result2); // Different source should return false
        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(source1Mock, retrievedSource); // Original source should remain
    }

    [Fact]
    public void SetSource_WhenCalledWithSameSource_ReturnsTrue()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();

        // Act
        var result1 = property.SetSource(sourceMock);
        var result2 = property.SetSource(sourceMock);

        // Assert
        Assert.True(result1);
        Assert.True(result2); // The same source should return true (idempotent)
    }

    [Fact]
    public void RemoveSource_WithMatchingSource_ClearsSourceReference()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();
        property.SetSource(sourceMock);

        // Act
        var removed = property.RemoveSource(sourceMock);

        // Assert
        Assert.True(removed);
        Assert.False(property.TryGetSource(out _));
    }

    [Fact]
    public void RemoveSource_WithDifferentSource_DoesNotClearSourceReference()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();
        var otherSourceMock = Mock.Of<ISubjectSource>();
        property.SetSource(sourceMock);

        // Act
        var removed = property.RemoveSource(otherSourceMock);

        // Assert
        Assert.False(removed);
        Assert.True(property.TryGetSource(out var source));
        Assert.Same(sourceMock, source);
    }

    [Fact]
    public async Task CommitAsync_WithSourceBoundProperty_WritesToSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = CreateSucceedingSource();

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithSourceWriteFailure_ThrowsTransactionException()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = CreateFailingSource("Write failed");

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<TransactionException>(
                () => transaction.CommitAsync(CancellationToken.None));

            // Assert
            Assert.Single(exception.FailedChanges);
            Assert.Single(exception.Errors);
            Assert.IsType<SourceTransactionWriteException>(exception.Errors[0]);
        }

        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithMixedSourceAndLocal_InBestEffortMode_AppliesLocalAndSuccessfulSource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var failingSource = CreateFailingSource("Write failed");

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        firstNameProp.SetSource(failingSource.Object);

        // Act
        // Use BestEffort mode to test partial success (local properties should be applied)
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var exception = await Assert.ThrowsAsync<TransactionException>(
                () => transaction.CommitAsync(CancellationToken.None));

            // Assert
            Assert.Single(exception.FailedChanges);
        }

        Assert.Null(person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task CommitAsync_WithMultipleSources_GroupsBySource()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source1Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source2Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(source1Mock.Object);
        lastNameProp.SetSource(source2Mock.Object);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal(1, source1Writes[0]);
        Assert.Equal(1, source2Writes[0]);
    }

    [Fact]
    public async Task CommitAsync_UserCancellationIsIgnored_CommitSucceeds()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<WriteResult>(WriteResult.Success));

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act - User cancellation is ignored during commit (only timeout matters)
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            // Commit should succeed even with cancelled token
            await transaction.CommitAsync(cts.Token);

            // Assert
            Assert.Equal("John", person.FirstName);
        }
    }

    [Fact]
    public async Task CommitAsync_WithCommitTimeout_TimesOutAndFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>(async (changes, ct) =>
            {
                // Simulate slow write that will be cancelled by timeout
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return WriteResult.Success;
                }
                catch (OperationCanceledException)
                {
                    return WriteResult.Failure(changes, new OperationCanceledException(ct));
                }
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        // Act - Use a very short timeout
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            commitTimeout: TimeSpan.FromMilliseconds(50)))
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<TransactionException>(
                () => transaction.CommitAsync());

            // Assert
            Assert.Single(exception.FailedChanges);
            Assert.Single(exception.Errors);
            var sourceWriteException = Assert.IsType<SourceTransactionWriteException>(exception.Errors[0]);
            Assert.IsAssignableFrom<OperationCanceledException>(sourceWriteException.InnerException);
        }
    }

    [Fact]
    public async Task Integration_WithMockSource_VerifyWriteChangesAsyncCalled()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);

        var capturedChanges = new List<SubjectPropertyChange>();
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                foreach (var change in changes.Span)
                {
                    capturedChanges.Add(change);
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(sourceMock.Object);
        lastNameProp.SetSource(sourceMock.Object);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(2, capturedChanges.Count);
        Assert.Contains(capturedChanges, c => c.Property.Metadata.Name == nameof(Person.FirstName) && c.GetNewValue<string>() == "John");
        Assert.Contains(capturedChanges, c => c.Property.Metadata.Name == nameof(Person.LastName) && c.GetNewValue<string>() == "Doe");

        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CommitAsync_WithMultipleContexts_ResolvesCallbacksPerContext()
    {
        // Arrange
        var context1 = CreateContext();
        var context2 = CreateContext();

        var person1 = new Person(context1);
        var person2 = new Person(context2);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source1Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source2Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person1, nameof(Person.FirstName)).SetSource(source1Mock.Object);
        new PropertyReference(person2, nameof(Person.FirstName)).SetSource(source2Mock.Object);

        // Act
        // Use separate transactions for each context since transactions are now context-bound
        using (var transaction1 = await context1.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person1.FirstName = "John";
            await transaction1.CommitAsync(CancellationToken.None);
        }

        using (var transaction2 = await context2.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person2.FirstName = "Jane";
            await transaction2.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal("John", person1.FirstName);
        Assert.Equal("Jane", person2.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithPartialWriteFailure_ReportsOnlyFailedChanges()
    {
        // Arrange
        // Tests the path where WriteResult.FailedChanges contains specific failed changes
        // (not all changes in the batch failed)
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                // Simulate partial failure: only FirstName fails
                var span = changes.Span;
                var failedChanges = new List<SubjectPropertyChange>();
                for (var i = 0; i < span.Length; i++)
                {
                    if (span[i].Property.Metadata.Name == nameof(Person.FirstName))
                    {
                        failedChanges.Add(span[i]);
                    }
                }

                if (failedChanges.Count > 0)
                {
                    return new ValueTask<WriteResult>(
                        WriteResult.PartialFailure(
                            failedChanges.ToArray(),
                            new InvalidOperationException("FirstName write failed")));
                }
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceMock.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        // Only FirstName should be in FailedChanges (partial failure)
        Assert.Single(ex.FailedChanges);
        Assert.Equal(nameof(Person.FirstName), ex.FailedChanges[0].Property.Metadata.Name);

        // LastName should have been applied (it was in successful changes)
        Assert.Equal("Doe", person.LastName);
        // FirstName should not be applied (it failed)
        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithPartialWriteFailure_InRollbackMode_RevertsSuccessfulChanges()
    {
        // Arrange
        // Tests that partial failure triggers rollback for successful changes
        var context = CreateContext();
        var person = new Person(context);

        var writeCallCount = 0;
        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                writeCallCount++;
                if (writeCallCount == 1)
                {
                    // First call: partial failure (only FirstName fails)
                    var span = changes.Span;
                    for (var i = 0; i < span.Length; i++)
                    {
                        if (span[i].Property.Metadata.Name == nameof(Person.FirstName))
                        {
                            return new ValueTask<WriteResult>(
                                WriteResult.PartialFailure(
                                    new[] { span[i] },
                                    new InvalidOperationException("FirstName write failed")));
                        }
                    }
                }
                // Subsequent calls (rollback): succeed
                return new ValueTask<WriteResult>(WriteResult.Success);
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceMock.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync(CancellationToken.None));

        // Assert
        // In Rollback mode, nothing should be applied when there's any failure
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Contains("Rollback was attempted", ex.Message);
        Assert.Equal(2, writeCallCount); // Initial write + revert
    }
}
