using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for transaction modes: BestEffort and Rollback.
/// </summary>
public class SubjectTransactionFailureHandlingTests : TransactionTestBase
{
    private sealed class ThrowingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Writer boom");

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    [Fact]
    public async Task WhenWriterThrows_ThenAllChangesFailAndTransactionIsTerminal()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();
        context.AddService<ITransactionWriter>(new ThrowingTransactionWriter());

        var person = new Person(context);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal("Writer boom", exception.InnerException!.Message);
        Assert.Null(person.FirstName); // local model untouched (writer threw before apply)
        Assert.Null(person.LastName);

        // A second commit is rejected: the transaction is terminal, not retryable.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Contains("already been committed", second.Message);
    }

    private sealed class ThrowingRevertTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            // Report the first change as written and the second as failed so the Rollback path
            // calls RevertSourceWritesAsync for the written change.
            var span = changes.Span;
            return new(new SourceWriteResult(
                Written: [span[0]],
                Failed: [span[1]],
                Errors: [new InvalidOperationException("Source boom")],
                RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Revert boom");
    }

    [Fact]
    public async Task WhenWriterThrowsDuringRevert_ThenRevertsFailAndTransactionIsTerminal()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();
        context.AddService<ITransactionWriter>(new ThrowingRevertTransactionWriter());

        var person = new Person(context);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(2, exception.FailedChanges.Count); // the failed source write and the failed revert
        Assert.Contains(exception.Errors, error => error.Message == "Source boom");
        Assert.Contains(exception.Errors, error => error.Message == "Revert boom");
        Assert.Null(person.FirstName); // local model untouched (Rollback applies nothing on source failure)
        Assert.Null(person.LastName);

        // A second commit is rejected: the transaction is terminal, not retryable.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Contains("already been committed", second.Message);
    }

    [Fact]
    public async Task BestEffortMode_AppliesSuccessfulChanges_WhenSomeSourcesFail()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var successSource = CreateSucceedingSource();
        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<SubjectTransactionException>(() => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Null(person.LastName);
        Assert.Single(ex.FailedChanges);
    }

    [Fact]
    public async Task RollbackMode_AppliesAllChanges_WhenAllSourcesSucceed()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var source1 = CreateSucceedingSource();
        var source2 = CreateSucceedingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(source1.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(source2.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        await tx.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task RollbackMode_RevertsSuccessfulSources_WhenAnySourceFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var writeCallCount = 0;
        var successSource = new Mock<ISubjectSource>();
        successSource.Setup(s => s.WriteBatchSize).Returns(0);
        successSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback(() => writeCallCount++)
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));

        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<SubjectTransactionException>(() => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
        Assert.Equal(2, writeCallCount); // Initial write + revert
        Assert.Contains("Rollback was attempted", ex.Message);
    }

    [Fact]
    public async Task RollbackMode_ReportsRevertFailures_WhenRevertAlsoFails()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        var callCount = 0;
        var successThenFailSource = new Mock<ISubjectSource>();
        successThenFailSource.Setup(s => s.WriteBatchSize).Returns(0);
        successThenFailSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                    return new ValueTask<WriteResult>(WriteResult.Success); // Initial write succeeds
                else
                    return new ValueTask<WriteResult>(WriteResult.Failure(changes, new InvalidOperationException("Revert failed"))); // Revert fails
            });

        var failSource = CreateFailingSource();

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(successThenFailSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        var ex = await Assert.ThrowsAsync<SubjectTransactionException>(() => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(2, ex.FailedChanges.Count);
    }

    [Fact]
    public async Task RollbackMode_ChangesWithoutSource_NotApplied_WhenSourceFails()
    {
        // Arrange
        // Tests that in Rollback mode, when source writes fail,
        // local changes (without source) are NOT applied and are reported as failed.
        var context = CreateContext();
        var person = new Person(context);

        var failSource = CreateFailingSource();

        // Only FirstName has a source, LastName has no source
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe"; // No source - not applied when source fails in Rollback mode

        var ex = await Assert.ThrowsAsync<SubjectTransactionException>(() => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        // Both changes should be in FailedChanges - FirstName failed to write, LastName wasn't applied
        Assert.Equal(2, ex.FailedChanges.Count);
        Assert.Empty(ex.AppliedChanges);

        // Nothing applied in rollback mode
        Assert.Null(person.FirstName);
        Assert.Null(person.LastName);
    }

    [Fact]
    public async Task BestEffortMode_ChangesWithoutSource_AlwaysApplied_WhenSourceFails()
    {
        // Arrange
        // Tests that changes without source are applied in BestEffort mode
        var context = CreateContext();
        var person = new Person(context);

        var failSource = CreateFailingSource();

        // Only FirstName has a source
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);

        // Act
        using var tx = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe"; // No source

        var ex = await Assert.ThrowsAsync<SubjectTransactionException>(() => tx.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        // In BestEffort, successful changes are applied
        Assert.Equal("Doe", person.LastName);
        Assert.Null(person.FirstName);
        Assert.Single(ex.FailedChanges);
    }
}
