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
    public async Task WhenSomeSourcesFailInBestEffortMode_ThenSuccessfulChangesAreApplied()
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
    public async Task WhenAllSourcesSucceedInRollbackMode_ThenAllChangesAreApplied()
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
    public async Task WhenAnySourceFailsInRollbackMode_ThenSuccessfulSourceWritesAreReverted()
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
    public async Task WhenSourceFailsInRollbackMode_ThenLocalChangesAreNotApplied()
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
    public async Task WhenSourceFailsInBestEffortMode_ThenLocalChangesAreStillApplied()
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

    private static bool ErrorMentions(Exception exception, string text)
    {
        // Walk the whole inner-exception chain: a source revert failure is wrapped twice
        // (SourceTransactionWriteException -> "Failed to rollback..." -> the source error),
        // so the message can sit deeper than the first inner exception.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(text))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task WhenSourceAndRevertFailInRollbackMode_ThenFailedChangesAndErrorsListExactContents()
    {
        // Arrange: FirstName's source write succeeds but its revert fails; LastName's source write fails.
        var context = CreateContext();
        var person = new Person(context);

        var callCount = 0;
        var failingRevertSource = new Mock<ISubjectSource>();
        failingRevertSource.Setup(s => s.WriteBatchSize).Returns(0);
        failingRevertSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns((ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken _) =>
            {
                callCount++;
                return callCount == 1
                    ? new ValueTask<WriteResult>(WriteResult.Success)
                    : new ValueTask<WriteResult>(WriteResult.Failure(changes, new InvalidOperationException("Revert boom")));
            });

        var failSource = CreateFailingSource("Write boom");

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failingRevertSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert: exact properties and error contents, not just counts.
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.FirstName)); // revert failed
        Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.LastName));  // source write failed
        Assert.Equal(2, exception.Errors.Count);
        Assert.Contains(exception.Errors, error => ErrorMentions(error, "Write boom"));
        Assert.Contains(exception.Errors, error => ErrorMentions(error, "Revert boom"));
        Assert.Null(person.FirstName); // local model untouched (Rollback applies nothing on source failure)
        Assert.Null(person.LastName);
    }

    [Fact]
    public async Task WhenApplyAndLocalRevertFailInRollbackMode_ThenFailedChangesListEveryStuckProperty()
    {
        // Arrange: FirstName succeeds at its source (and must be source-reverted later);
        // PropertyB fails its local apply, which triggers rollback; PropertyA applies, then
        // fails its local revert (sequence-based: apply is its first setter call, revert the second).
        var context = CreateContext();
        var person = new Person(context);
        var propertyACalls = 0;
        var device = new ThrowingDevice(context);
        device.ShouldThrow = property =>
        {
            if (property == nameof(ThrowingDevice.PropertyB))
            {
                return true;
            }
            propertyACalls++;
            return propertyACalls > 1;
        };

        var writeCount = 0;
        var revertedSource = new Mock<ISubjectSource>();
        revertedSource.Setup(s => s.WriteBatchSize).Returns(0);
        revertedSource.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback(() => writeCount++)
            .Returns((ReadOnlyMemory<SubjectPropertyChange> _, CancellationToken _) =>
                new ValueTask<WriteResult>(WriteResult.Success));
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(revertedSource.Object);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        device.PropertyA = true;
        device.PropertyB = true;
        device.ThrowingEnabled = true;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(ThrowingDevice.PropertyB)); // failed apply
        Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(ThrowingDevice.PropertyA)); // failed local revert
        // FirstName was written to its source and successfully reverted there; it is intentionally
        // NOT reported in FailedChanges (the written set is excluded from rolled-back locals).
        Assert.DoesNotContain(exception.FailedChanges, change => change.Property.Name == nameof(Person.FirstName));
        Assert.Equal(2, exception.Errors.Count); // PropertyB apply error and PropertyA revert error
        Assert.Equal(2, writeCount);             // source written, then reverted
        Assert.True(device.PropertyA);            // stuck: local revert failed, applied value remains
        Assert.False(device.PropertyB);           // never applied
        Assert.Null(person.FirstName);            // local apply rolled back
    }

    [Fact]
    public async Task WhenSourceFailsInOptimisticTransaction_ThenCommitFailsTerminallyAndLockIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);
        var failSource = CreateFailingSource("Source boom");
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);

        var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, TransactionLocking.Optimistic);
        person.FirstName = "John";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Null(person.FirstName);
        Assert.Empty(exception.AppliedChanges);
        var failed = Assert.Single(exception.FailedChanges);
        Assert.Equal(nameof(Person.FirstName), failed.Property.Name);
        Assert.Contains(exception.Errors, error => ErrorMentions(error, "Source boom"));

        // Terminal: a second commit is rejected.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Contains("already been committed", second.Message);
        transaction.Dispose();

        // The optimistic commit lock was released: a new transaction can begin and commit.
        using var nextTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.LastName = "Doe";
        await nextTransaction.CommitAsync(CancellationToken.None);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenSourceBatchOfTwoChangesFails_ThenTwoFailedChangesShareOneError()
    {
        // Arrange: one source, two properties; the source fails the whole batch with one exception.
        var context = CreateContext();
        var person = new Person(context);
        var failSource = CreateFailingSource("Batch boom");
        new PropertyReference(person, nameof(Person.FirstName)).SetSource(failSource.Object);
        new PropertyReference(person, nameof(Person.LastName)).SetSource(failSource.Object);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert: FailedChanges is per change, Errors is per failed source batch.
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.FirstName));
        Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.LastName));
        var error = Assert.Single(exception.Errors);
        Assert.True(ErrorMentions(error, "Batch boom"));
    }
}
