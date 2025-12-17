using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

/// <summary>
/// Tests for source integration: SetSource, TryGetSource, WriteChangesAsync, and multi-context scenarios.
/// </summary>
public class SubjectTransactionSourceTests : TransactionTestBase
{
    [Fact]
    public void SetSource_StoresSourceReference()
    {
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();

        property.SetSource(sourceMock);

        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(sourceMock, retrievedSource);
    }

    [Fact]
    public void TryGetSource_WhenNoSourceSet_ReturnsFalse()
    {
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        var result = property.TryGetSource(out var source);

        Assert.False(result);
        Assert.Null(source);
    }

    [Fact]
    public void SetSource_WhenCalledMultipleTimes_ReplacesReference()
    {
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source1Mock = Mock.Of<ISubjectSource>();
        var source2Mock = Mock.Of<ISubjectSource>();

        property.SetSource(source1Mock);
        property.SetSource(source2Mock);

        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(source2Mock, retrievedSource);
        Assert.NotSame(source1Mock, retrievedSource);
    }

    [Fact]
    public void RemoveSource_ClearsSourceReference()
    {
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();
        property.SetSource(sourceMock);

        property.RemoveSource();

        Assert.False(property.TryGetSource(out _));
    }

    [Fact]
    public async Task CommitAsync_WithSourceBoundProperty_WritesToSource()
    {
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = CreateSucceedingSource();

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        using (var transaction = await context.BeginExclusiveTransactionAsync())
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithSourceWriteFailure_ThrowsTransactionException()
    {
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = CreateFailingSource("Write failed");

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        using (var transaction = await context.BeginExclusiveTransactionAsync())
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<TransactionException>(
                () => transaction.CommitAsync(CancellationToken.None));

            Assert.Single(exception.FailedChanges);
            Assert.IsType<SourceTransactionWriteException>(exception.FailedChanges[0].Error);
        }

        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithMixedSourceAndLocal_InBestEffortMode_AppliesLocalAndSuccessfulSource()
    {
        var context = CreateContext();
        var person = new Person(context);

        var failingSource = CreateFailingSource("Write failed");

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        firstNameProp.SetSource(failingSource.Object);

        // Use BestEffort mode to test partial success (local properties should be applied)
        using (var transaction = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var exception = await Assert.ThrowsAsync<TransactionException>(
                () => transaction.CommitAsync(CancellationToken.None));

            Assert.Single(exception.FailedChanges);
        }

        Assert.Null(person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task CommitAsync_WithMultipleSources_GroupsBySource()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source1Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source2Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(source1Mock.Object);
        lastNameProp.SetSource(source2Mock.Object);

        using (var transaction = await context.BeginExclusiveTransactionAsync())
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal(1, source1Writes[0]);
        Assert.Equal(1, source2Writes[0]);
    }

    [Fact]
    public async Task CommitAsync_WhenCancelled_PropagatesCancellation()
    {
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, ct) =>
            {
                if (ct.IsCancellationRequested)
                    return new ValueTask<WriteResult>(WriteResult.Failure(new OperationCanceledException(ct)));
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using (var transaction = await context.BeginExclusiveTransactionAsync())
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<TransactionException>(
                () => transaction.CommitAsync(cts.Token));

            Assert.Single(exception.FailedChanges);
            var sourceWriteException = Assert.IsType<SourceTransactionWriteException>(exception.FailedChanges[0].Error);
            Assert.IsType<OperationCanceledException>(sourceWriteException.InnerException);
        }
    }

    [Fact]
    public async Task Integration_WithMockSource_VerifyWriteChangesAsyncCalled()
    {
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
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(sourceMock.Object);
        lastNameProp.SetSource(sourceMock.Object);

        using (var transaction = await context.BeginExclusiveTransactionAsync())
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            await transaction.CommitAsync(CancellationToken.None);
        }

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
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                source2Writes.Add(changes.Length);
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        new PropertyReference(person1, nameof(Person.FirstName)).SetSource(source1Mock.Object);
        new PropertyReference(person2, nameof(Person.FirstName)).SetSource(source2Mock.Object);

        // Use separate transactions for each context since transactions are now context-bound
        using (var transaction1 = await context1.BeginExclusiveTransactionAsync())
        {
            person1.FirstName = "John";
            await transaction1.CommitAsync(CancellationToken.None);
        }

        using (var transaction2 = await context2.BeginExclusiveTransactionAsync())
        {
            person2.FirstName = "Jane";
            await transaction2.CommitAsync(CancellationToken.None);
        }

        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal("John", person1.FirstName);
        Assert.Equal("Jane", person2.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithPartialWriteFailure_ReportsOnlyFailedChanges()
    {
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
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceMock.Object);

        using var tx = await context.BeginExclusiveTransactionAsync(TransactionMode.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        // Only FirstName should be in FailedChanges (partial failure)
        Assert.Single(ex.FailedChanges);
        Assert.Equal(nameof(Person.FirstName), ex.FailedChanges[0].Change.Property.Metadata.Name);

        // LastName should have been applied (it was in successful changes)
        Assert.Equal("Doe", person.LastName);
        // FirstName should not be applied (it failed)
        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithPartialWriteFailure_InRollbackMode_RevertsSuccessfulChanges()
    {
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
                return new ValueTask<WriteResult>(WriteResult.Success());
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(sourceMock.Object);

        using var tx = await context.BeginExclusiveTransactionAsync(TransactionMode.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<TransactionException>(() => tx.CommitAsync());

        // In Rollback mode, nothing should be applied when there's any failure
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Contains("Rollback was attempted", ex.Message);
        Assert.Equal(2, writeCallCount); // Initial write + revert
    }
}
