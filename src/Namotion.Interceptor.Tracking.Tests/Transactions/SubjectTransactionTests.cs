using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
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
            .WithPropertyChangeSubscriptions();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        });

        Assert.Contains("WithTransactions()", exception.Message);
    }

    [Fact]
    public async Task WhenTheBoundTransactionInterceptorIsInheritedAndSecond_ThenTheWriteIsCapturedInsteadOfThrowing()
    {
        // Arrange: context inheritance adds another context as a fallback on attach, so a subject built on
        // one transaction-enabled context and attached into a graph rooted on another resolves two. The
        // subject's own interceptor is first and the fallback's interceptor is second, so binding must scan
        // all resolved instances by reference rather than accepting only the first one.
        var context = CreateTransactionContext();
        var fallbackContext = CreateTransactionContext();

        var person = new Person(context);
        ((IInterceptorSubject)person).Context.AddFallbackContext(fallbackContext);

        // Act
        using (var transaction = await fallbackContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";

            // Assert: captured rather than written straight through, so the binding resolved to this
            // context. Reading the property here would serve the pending value either way.
            Assert.Single(transaction.GetPendingChanges(),
                change => change.Property.Name == nameof(Person.FirstName));

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task WhenTransactionIsBoundToAnUnrelatedContext_ThenWriteThrows()
    {
        // Arrange
        var subjectContext = CreateTransactionContext();
        var transactionContext = CreateTransactionContext();
        var person = new Person(subjectContext);

        using var transaction = await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "John");

        // Assert
        Assert.Contains("Transaction is bound to a different context", exception.Message);
        Assert.Empty(transaction.GetPendingChanges());
        Assert.Null(person.FirstName);
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
    public async Task WhenEmptyCommitIsTerminal_ThenLaterAccessUsesTheModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        await transaction.CommitAsync(CancellationToken.None);

        // Act
        person.FirstName = "landed-after-empty-commit";
        string? landedValue = null;
        await RunWithoutAsyncLocalFlowAsync(() => landedValue = person.FirstName);
        await RunWithoutAsyncLocalFlowAsync(() => person.FirstName = "external-after-empty-commit");

        // Assert
        Assert.Equal("landed-after-empty-commit", landedValue);
        Assert.Equal("external-after-empty-commit", person.FirstName);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenSuccessfulCommitIsTerminal_ThenLaterAccessUsesTheModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "model" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "committed";
        await transaction.CommitAsync(CancellationToken.None);

        // Act
        person.LastName = "landed-after-commit";
        string? landedValue = null;
        await RunWithoutAsyncLocalFlowAsync(() => landedValue = person.LastName);
        await RunWithoutAsyncLocalFlowAsync(() => person.LastName = "external-after-commit");

        // Assert
        Assert.Equal("committed", person.FirstName);
        Assert.Equal("landed-after-commit", landedValue);
        Assert.Equal("external-after-commit", person.LastName);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenCommitFailureIsTerminal_ThenLaterAccessUsesTheModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var subject = new TransactionCascadeSubject(context) { Plain = "model" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "applied";
        subject.Failing = "fails";
        subject.ThrowOnFailingWrite = true;
        await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        subject.SideEffect = "landed-after-terminal-failure";
        string? landedValue = null;
        await RunWithoutAsyncLocalFlowAsync(() => landedValue = subject.SideEffect);
        await RunWithoutAsyncLocalFlowAsync(() => subject.SideEffect = "external-after-terminal-failure");

        // Assert
        Assert.Equal("applied", subject.Plain);
        Assert.Equal("landed-after-terminal-failure", landedValue);
        Assert.Equal("external-after-terminal-failure", subject.SideEffect);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenCommitFailureIsRetryable_ThenLaterWritesRemainCaptured()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "original" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);
        person.FirstName = "pending";
        await RunWithoutAsyncLocalFlowAsync(() => person.FirstName = "external");
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        person.LastName = "captured-after-conflict";

        // Assert
        Assert.Equal("captured-after-conflict", person.LastName);
        Assert.Contains(transaction.GetPendingChanges(),
            change => change.Property.Name == nameof(Person.LastName));

        string? modelLastName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelLastName = person.LastName);
        Assert.Null(modelLastName);

        await RunWithoutAsyncLocalFlowAsync(() => person.FirstName = "original");
        await transaction.CommitAsync(CancellationToken.None);
        Assert.Equal("pending", person.FirstName);
        Assert.Equal("captured-after-conflict", person.LastName);
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
        // Both sides block until the other reaches its gate, and the waits below are unbounded, so a
        // participant left in the pool queue would hang the run rather than fail it.
        var task1 = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
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
        var task2 = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
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
        // Neither side can reach the countdown unless both are actually running.
        var task1 = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
        {
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Optimistic,
                conflictBehavior: TransactionConflictBehavior.Ignore))
            {
                bothStarted.Signal();
                Assert.True(
                    bothStarted.Wait(TimeSpan.FromSeconds(5)),
                    "The second optimistic transaction did not start within five seconds.");
                await t.CommitAsync(CancellationToken.None);
            }
        });

        var task2 = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(async () =>
        {
            using (var t = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Optimistic,
                conflictBehavior: TransactionConflictBehavior.Ignore))
            {
                bothStarted.Signal();
                Assert.True(
                    bothStarted.Wait(TimeSpan.FromSeconds(5)),
                    "The first optimistic transaction did not start within five seconds.");
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

        var disposedTransaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Exclusive);
        disposedTransaction.Dispose();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var transaction = await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Exclusive);
            await transaction.CommitAsync(CancellationToken.None);
        });

        // Assert
        Assert.Null(exception);
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

}
