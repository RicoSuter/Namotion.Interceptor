using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionTests
{
    private static async Task RunWithoutAsyncLocalFlowAsync(Action action)
    {
        Task task;
        var flowControl = ExecutionContext.SuppressFlow();
        try
        {
            task = Task.Run(action);
        }
        finally
        {
            flowControl.Undo();
        }

        await task;
    }

    private static IInterceptorSubjectContext CreateTransactionContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
    }

    [Fact]
    public async Task WhenTransactionsNotEnabled_ThenBeginThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithPropertyChangeObservable();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        });

        Assert.Contains("WithTransactions()", exception.Message);
    }

    [Fact]
    public async Task WhenTransactionCommitted_ThenChangesAreApplied()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenTransactionDisposedWithoutCommit_ThenChangesAreDiscarded()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Modified";
        }

        // Assert
        Assert.Equal("Original", person.FirstName);
    }

    [Fact]
    public async Task WhenReadingPropertyDuringTransaction_ThenPendingValueIsReturned()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        // Act & Assert
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Pending";
            Assert.Equal("Pending", person.FirstName);
            await transaction.CommitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenSamePropertyWrittenTwice_ThenLastWriteWinsAndOriginalOldValuePreserved()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "First";
            person.FirstName = "Second";

            var pending = transaction.GetPendingChanges();
            Assert.Single(pending);
            Assert.Equal("Original", pending[0].GetOldValue<string?>());
            Assert.Equal("Second", pending[0].GetNewValue<string?>());

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("Second", person.FirstName);
    }

    [Fact]
    public async Task WhenCommittedWithNoChanges_ThenSucceeds()
    {
        // Arrange
        var context = CreateTransactionContext();

        // Act & Assert
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            var commitTask = transaction.CommitAsync(CancellationToken.None);
            Assert.True(commitTask.IsCompletedSuccessfully, "Empty commit should complete synchronously.");
            await commitTask;
            Assert.Empty(transaction.GetPendingChanges());
        }
    }

    [Fact]
    public async Task WhenConflictDetected_ThenConflictExceptionThrown()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            person.FirstName = "InTransaction";

            await RunWithoutAsyncLocalFlowAsync(() => { person.FirstName = "ExternalChange"; });

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());

            Assert.Single(ex.ConflictingProperties);
            Assert.Equal(nameof(Person.FirstName), ex.ConflictingProperties[0].Name);
            Assert.Contains(nameof(Person.FirstName), ex.Message);
            Assert.Empty(ex.AppliedChanges);
            Assert.Empty(ex.FailedChanges);
        }
    }

    [Fact]
    public async Task WhenConflictBehaviorIsIgnore_ThenNoConflictException()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.Ignore))
        {
            person.FirstName = "InTransaction";

            await RunWithoutAsyncLocalFlowAsync(() => { person.FirstName = "ExternalChange"; });

            // Act
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("InTransaction", person.FirstName);
    }

    [Fact]
    public async Task WhenTransactionAlreadyCommitted_ThenCommitAgainThrows()
    {
        // Arrange: a successful non-empty commit, so the committed state comes from the
        // full commit path rather than the empty-commit early return.
        var context = CreateTransactionContext();
        var person = new Person(context);

        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
            Assert.Equal("John", person.FirstName);
            Assert.Empty(transaction.GetPendingChanges());

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains("already been committed", exception.Message);
        }
    }

    [Fact]
    public async Task WhenTransactionDisposed_ThenCommitThrows()
    {
        // Arrange
        var context = CreateTransactionContext();
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        transaction.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task WhenNestedTransactionAttempted_ThenThrows()
    {
        // Arrange
        var context = CreateTransactionContext();

        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
            });
            Assert.Contains("Nested transactions are not supported", exception.Message);
        }
    }

    [Fact]
    public async Task WhenExclusiveLocking_ThenSecondTransactionWaits()
    {
        // Arrange
        var context = CreateTransactionContext();
        var order = new List<int>();
        var task1CanCommit = new ManualResetEventSlim(false);
        var task2Waiting = new ManualResetEventSlim(false);

        // Act
        var task1 = Task.Run(async () =>
        {
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Exclusive))
            {
                lock (order) { order.Add(1); }
                task2Waiting.Set();
                task1CanCommit.Wait();
                await t.CommitAsync(CancellationToken.None);
            }
        });

        task2Waiting.Wait();
        var task2 = Task.Run(async () =>
        {
            task1CanCommit.Set();
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Exclusive))
            {
                lock (order) { order.Add(2); }
                await t.CommitAsync(CancellationToken.None);
            }
        });

        await Task.WhenAll(task1, task2);

        // Assert
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task WhenOptimisticLocking_ThenBothTransactionsCanBegin()
    {
        // Arrange
        var context = CreateTransactionContext();
        var bothStarted = new CountdownEvent(2);

        // Act
        var task1 = Task.Run(async () =>
        {
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Optimistic,
                conflictBehavior: TransactionConflictBehavior.Ignore))
            {
                bothStarted.Signal();
                bothStarted.Wait(TimeSpan.FromSeconds(5));
                await t.CommitAsync(CancellationToken.None);
            }
        });

        var task2 = Task.Run(async () =>
        {
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Optimistic,
                conflictBehavior: TransactionConflictBehavior.Ignore))
            {
                bothStarted.Signal();
                bothStarted.Wait(TimeSpan.FromSeconds(5));
                await t.CommitAsync(CancellationToken.None);
            }
        });

        // Assert
        await Task.WhenAll(task1, task2);
    }

    [Fact]
    public async Task WhenTransactionCommitted_ThenObservableNotificationsFire()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = CreateTransactionContext();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context);
        changes.Clear();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            Assert.Empty(changes);

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FirstName) &&
            c.GetNewValue<string?>() == "John");
    }

    [Fact]
    public async Task WhenTransactionDisposedWithoutCommit_ThenNoObservableNotificationsFire()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = CreateTransactionContext();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context) { FirstName = "Original" };
        changes.Clear();

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "Modified";
        }

        // Assert
        Assert.Empty(changes);
    }

    [Fact]
    public async Task WhenGetPendingChangesCalled_ThenReturnsSnapshotOfPendingChanges()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var pending = transaction.GetPendingChanges();

            // Assert
            Assert.Equal(2, pending.Count);
            Assert.Contains(pending, c => c.Property.Name == nameof(Person.FirstName));
            Assert.Contains(pending, c => c.Property.Name == nameof(Person.LastName));
        }
    }

    [Fact]
    public async Task WhenDisposeCalledMultipleTimes_ThenIsIdempotent()
    {
        // Arrange
        var context = CreateTransactionContext();
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        Assert.Same(transaction, SubjectTransaction.Current);

        // Act & Assert
        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);

        transaction.Dispose();
        Assert.Null(SubjectTransaction.Current);
    }

    [Fact]
    public async Task WhenMultipleSubjectsModified_ThenAllChangesCommittedAtomically()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person1 = new Person(context);
        var person2 = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person1.FirstName = "Alice";
            person2.FirstName = "Bob";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("Alice", person1.FirstName);
        Assert.Equal("Bob", person2.FirstName);
    }

    [Fact]
    public async Task WhenExclusiveTransactionDisposed_ThenLockIsReleased()
    {
        // Arrange
        var context = CreateTransactionContext();

        using (await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Exclusive))
        {
        }

        // Act & Assert
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Exclusive))
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenDerivedPropertyRead_ThenReflectsPendingChanges()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            // Assert
            Assert.Equal("John Doe", person.FullName);

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal("John Doe", person.FullName);
    }

    [Fact]
    public async Task WhenConflictExceptionThrown_ThenConflictingPropertiesAreReported()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original1", LastName = "Original2" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            person.FirstName = "InTx1";
            person.LastName = "InTx2";

            await RunWithoutAsyncLocalFlowAsync(() =>
            {
                person.FirstName = "External1";
                person.LastName = "External2";
            });

            // Act & Assert
            var ex = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());

            Assert.Equal(2, ex.ConflictingProperties.Count);
            Assert.Contains(ex.ConflictingProperties, p => p.Name == nameof(Person.FirstName));
            Assert.Contains(ex.ConflictingProperties, p => p.Name == nameof(Person.LastName));
        }
    }

    [Fact]
    public async Task WhenPropertyNotChangedExternally_ThenNoConflictDetected()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Original" };

        // Act
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            person.FirstName = "Modified";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal("Modified", person.FirstName);
    }

    [Fact]
    public async Task WhenCommittingFromDifferentAsyncFlow_ThenThrows()
    {
        // Arrange: begin in a separate flow so its AsyncLocal current-transaction does not reach here.
        var context = CreateTransactionContext();
        var transaction = await Task.Run(async () =>
            await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort));

        // Act & Assert
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Contains("async flow", exception.Message);
        }
        finally
        {
            transaction.Dispose();
        }
    }

    [Fact]
    public async Task WhenDisposingFromDifferentAsyncFlow_ThenOtherTransactionSlotIsNotCleared()
    {
        // Arrange
        var context = CreateTransactionContext();

        // transaction1 is begun in a separate flow, so its SetCurrent does not reach this flow.
        // Optimistic locking means neither transaction takes the lock at begin, so they can coexist.
        var transaction1 = await Task.Run(async () =>
            await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic));

        using var transaction2 = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic);
        Assert.Same(transaction2, SubjectTransaction.Current);

        // Act: disposing transaction1 from transaction2's flow must not clear transaction2's slot.
        transaction1.Dispose();

        // Assert
        Assert.Same(transaction2, SubjectTransaction.Current);
    }

    /// <summary>
    /// Reports every change as written but with each change's snapshot index shifted onto a different
    /// property (a left rotation), so the indices do not locate the written changes. Records the written
    /// set it was asked to revert.
    /// </summary>
    private sealed class WrongIndicesTransactionWriter : ITransactionWriter
    {
        public IReadOnlyList<SubjectPropertyChange>? RevertedWritten { get; private set; }

        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            ReadOnlyMemory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            var span = changes.Span;
            var written = new SubjectPropertyChange[span.Length];
            var indices = new int[span.Length];
            for (var i = 0; i < span.Length; i++)
            {
                written[i] = span[i];
                // Rotate indices left by one so each written change is pointed at a neighbour with a
                // different property (with at least two changes this guarantees a property mismatch).
                indices[i] = (i + 1) % span.Length;
            }

            return new ValueTask<SourceWriteResult>(new SourceWriteResult(
                Written: written,
                WrittenIndices: indices,
                Failed: [],
                Errors: [],
                RevertState: new object()));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
        {
            RevertedWritten = written;
            return new ValueTask<SourceRevertResult>(new SourceRevertResult([], []));
        }
    }

    [Fact]
    public async Task WhenWriterReturnsMismatchedIndices_ThenCommitFailsTerminallyAfterReverting()
    {
        // Arrange
        var context = CreateTransactionContext();
        var writer = new WrongIndicesTransactionWriter();
        context.AddService<ITransactionWriter>(writer);

        var person = new Person(context);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "John";
        person.LastName = "Doe";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Contains("indices", exception.Message);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(2, exception.FailedChanges.Count); // every snapshot change reported as failed
        Assert.NotNull(writer.RevertedWritten); // reverts were attempted with the written set
        Assert.Equal(2, writer.RevertedWritten!.Count);
        Assert.Null(person.FirstName); // local model untouched (nothing applied)
        Assert.Null(person.LastName);

        // Terminal: a second commit is rejected, not retried.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Contains("already been committed", second.Message);
    }
}
