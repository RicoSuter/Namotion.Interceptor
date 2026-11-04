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
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionTests
{
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
        var context = CreateContext();
        var fallbackContext = CreateContext();

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
        var subjectContext = CreateContext();
        var transactionContext = CreateContext();
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
        var context = CreateContext();
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
        var context = CreateContext();
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
        var context = CreateContext();
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
        var context = CreateContext();
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
        var context = CreateContext();

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
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Original" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            person.FirstName = "InTransaction";

            await RunOnThreadPoolWithoutFlowAsync(() => { person.FirstName = "ExternalChange"; });

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
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Original" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.Ignore))
        {
            person.FirstName = "InTransaction";

            await RunOnThreadPoolWithoutFlowAsync(() => { person.FirstName = "ExternalChange"; });

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
        var context = CreateContext();
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
        var context = CreateContext();
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        await transaction.CommitAsync(CancellationToken.None);

        // Act
        person.FirstName = "landed-after-empty-commit";
        string? landedValue = null;
        await RunOnThreadPoolWithoutFlowAsync(() => landedValue = person.FirstName);
        await RunOnThreadPoolWithoutFlowAsync(() => person.FirstName = "external-after-empty-commit");

        // Assert
        Assert.Equal("landed-after-empty-commit", landedValue);
        Assert.Equal("external-after-empty-commit", person.FirstName);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenSuccessfulCommitIsTerminal_ThenLaterAccessUsesTheModel()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "model" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "committed";
        await transaction.CommitAsync(CancellationToken.None);

        // Act
        person.LastName = "landed-after-commit";
        string? landedValue = null;
        await RunOnThreadPoolWithoutFlowAsync(() => landedValue = person.LastName);
        await RunOnThreadPoolWithoutFlowAsync(() => person.LastName = "external-after-commit");

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
        var context = CreateContext();
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
        await RunOnThreadPoolWithoutFlowAsync(() => landedValue = subject.SideEffect);
        await RunOnThreadPoolWithoutFlowAsync(() => subject.SideEffect = "external-after-terminal-failure");

        // Assert
        Assert.Equal("applied", subject.Plain);
        Assert.Equal("landed-after-terminal-failure", landedValue);
        Assert.Equal("external-after-terminal-failure", subject.SideEffect);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenApplyFailsInBestEffortWithoutHandlers_ThenSuccessfulAppliesAreKept()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TransactionCascadeSubject(context) { Plain = "model", Failing = "model" };

        // Act
        SubjectTransactionException exception;
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            // The failing change comes first, so the applies continue after it.
            subject.Failing = "fails";
            subject.Plain = "applied";
            subject.ThrowOnFailingWrite = true;
            exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Equal("applied", subject.Plain);
        Assert.Equal("model", subject.Failing);
        Assert.Equal([nameof(TransactionCascadeSubject.Plain)], exception.AppliedChanges.Select(change => change.Property.Name));
        Assert.Equal([nameof(TransactionCascadeSubject.Failing)], exception.FailedChanges.Select(change => change.Property.Name));
        Assert.Contains("Successful changes have been applied", exception.Message);
    }

    [Fact]
    public async Task WhenCommitFailureIsRetryable_ThenLaterWritesRemainCaptured()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "original" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);
        person.FirstName = "pending";
        await RunOnThreadPoolWithoutFlowAsync(() => person.FirstName = "external");
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        person.LastName = "captured-after-conflict";

        // Assert
        Assert.Equal("captured-after-conflict", person.LastName);
        Assert.Contains(transaction.GetPendingChanges(),
            change => change.Property.Name == nameof(Person.LastName));

        string? modelLastName = null;
        await RunOnThreadPoolWithoutFlowAsync(() => modelLastName = person.LastName);
        Assert.Null(modelLastName);

        await RunOnThreadPoolWithoutFlowAsync(() => person.FirstName = "original");
        await transaction.CommitAsync(CancellationToken.None);
        Assert.Equal("pending", person.FirstName);
        Assert.Equal("captured-after-conflict", person.LastName);
    }

    [Fact]
    public async Task WhenTransactionDisposed_ThenCommitThrows()
    {
        // Arrange
        var context = CreateContext();
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
        var context = CreateContext();

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

    [Theory]
    [InlineData(-5)]
    [InlineData(-2)]
    [InlineData(-1.5)]
    [InlineData(-0.5)]
    [InlineData(4294967295)]
    public async Task WhenCommitTimeoutIsOutOfRange_ThenBeginThrows(double milliseconds)
    {
        // Arrange
        var context = CreateContext();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await context.BeginTransactionAsync(
                TransactionFailureHandling.BestEffort,
                TransactionLocking.Exclusive,
                commitTimeout: TimeSpan.FromMilliseconds(milliseconds));
        });
        Assert.Null(SubjectTransaction.Current);

        // A leaked exclusive lock would block this begin, so the bound turns a hang into a failure.
        using var lockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var nextTransaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, TransactionLocking.Exclusive, cancellationToken: lockTimeout.Token);
        Assert.Same(nextTransaction, SubjectTransaction.Current);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WhenCommitTimeoutIsZeroOrInfinite_ThenBeginSucceeds(double milliseconds)
    {
        // Arrange
        var context = CreateContext();

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, commitTimeout: TimeSpan.FromMilliseconds(milliseconds));

        // Assert
        Assert.Same(transaction, SubjectTransaction.Current);
    }

    [Fact]
    public async Task WhenCommitTimeoutIsTheTimerMaximum_ThenCommitSucceeds()
    {
        // Arrange
        var context = CreateContext();
        context.AddService<ITransactionWriter>(new CountingTransactionWriter());
        var person = new Person(context) { FirstName = "Ann" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, commitTimeout: TimeSpan.FromMilliseconds(uint.MaxValue - 1));
        person.FirstName = "Bob";

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenCommitWithoutWriterFails_ThenCommitAsyncReturnsFaultedTask()
    {
        // Arrange
        var context = CreateContext(out var handler);
        handler.RejectCommit = true;
        var person = new Person(context) { FirstName = "Ann" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "Bob";

        // Act
        var commitTask = transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.True(commitTask.IsFaulted);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => commitTask.AsTask());
        Assert.Equal("rejected", exception.Message);
        handler.RejectCommit = false;
        await transaction.CommitAsync(CancellationToken.None);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenExclusiveLocking_ThenSecondTransactionWaits()
    {
        // Arrange
        var context = CreateContext();
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
        var context = CreateContext();
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
        var context = CreateContext();

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
        var context = CreateContext();

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
        var context = CreateContext();
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
        var context = CreateContext();
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
        var context = CreateContext();
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
        var context = CreateContext();

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
        var context = CreateContext();
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
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Original1", LastName = "Original2" };

        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            person.FirstName = "InTx1";
            person.LastName = "InTx2";

            await RunOnThreadPoolWithoutFlowAsync(() =>
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
        var context = CreateContext();
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
    public async Task WhenPropertyIsWrittenBackToLandedValue_ThenCommitKeepsLandedValueAndPublishesNothing()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = CreateContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var person = new Person(context) { FirstName = "Ann" };
        changes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        person.FirstName = "Ann";
        var valueInsideTransaction = person.FirstName;
        var pendingBeforeCommit = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Ann", valueInsideTransaction);
        Assert.Empty(pendingBeforeCommit);
        Assert.Equal("Ann", person.FirstName);
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(Person.FirstName));
    }

    [Fact]
    public async Task WhenLandedValueIsWrittenWithoutPendingChange_ThenNothingIsCaptured()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Ann";

        // Assert
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenPropertyIsRewrittenAfterWriteBack_ThenItIsCapturedAsNewChange()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann" };

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);
        person.FirstName = "Bob";
        person.FirstName = "Ann";
        person.LastName = "Smith";
        RunOutsideTransactionFlow(() => person.FirstName = "External");
        person.FirstName = "Carl";

        // Act
        var pending = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal([nameof(Person.LastName), nameof(Person.FirstName)], pending.Select(c => c.Property.Name));
        Assert.Equal("External", pending[1].GetOldValue<string?>());
        Assert.Equal("Carl", person.FirstName);
        Assert.Equal("Smith", person.LastName);
    }

    [Fact]
    public async Task WhenInterceptorBeforeTransactionWritesAnotherProperty_ThenWriteBackUndoesThatWriteToo()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithEqualityCheck()
            .WithService(() => new LastNameMirrorInterceptor())
            .WithTransactions();
        var person = new Person(context) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var pendingAfterWrite = transaction.GetPendingChanges();
        person.FirstName = "Ann";
        var pendingBeforeCommit = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Contains(pendingAfterWrite, c => c.Property.Name == nameof(Person.LastName));
        Assert.Empty(pendingBeforeCommit);
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("mirror-Ann", person.LastName);
    }

    [Fact]
    public async Task WhenPendingValueIsWrittenAgainAfterItLandedOutside_ThenChangeStaysPendingAndConflicts()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann" };

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);
        person.FirstName = "Bob";
        RunOutsideTransactionFlow(() => person.FirstName = "Bob");
        person.FirstName = "Bob";
        RunOutsideTransactionFlow(() => person.FirstName = "Eve");

        // Act & Assert
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task WhenInterceptorNormalizesFirstWriteToLandedValue_ThenItIsNotCaptured()
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out _, writer).WithService(() => new UpperCaseInterceptor());
        var person = new Person(context) { FirstName = "ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "ann";
        var pendingChanges = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Empty(pendingChanges);
        Assert.Empty(writer.ReceivedPropertyNames);
    }

    [RunsBefore(typeof(SubjectTransactionInterceptor))]
    private sealed class UpperCaseInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.NewValue is string value)
            {
                context.NewValue = (TProperty)(object)value.ToUpperInvariant();
            }

            next(ref context);
        }
    }

    [RunsBefore(typeof(SubjectTransactionInterceptor))]
    private sealed class LastNameMirrorInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.Property.Name == nameof(Person.FirstName) && context.Property.Subject is Person person)
            {
                person.LastName = "mirror-" + context.NewValue;
            }

            next(ref context);
        }
    }
}
