using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
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

    [Fact]
    public async Task WhenAmbientTransactionIsDisposedAndAnotherIsOpen_ThenWriteAppliesToModelAndIsNotCapturedByTheOtherTransaction()
    {
        // Arrange: capture the flow while a transaction is current, then dispose that transaction. The
        // captured flow keeps pointing at the disposed transaction, which is what any thread started
        // inside a transaction sees for its whole life (an Rx EventLoopScheduler worker, for example).
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Old" };

        var disposedTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        disposedTransaction.Dispose();

        // The second transaction keeps the process-wide active count above zero, so the interceptor does
        // not take its no-transaction fast path.
        using var openTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.LastName = "Doe";

        // Act
        ExecutionContext.Run(frozenFlow, _ => person.FirstName = "New", null);

        // Assert: the write went straight to the model instead of into the pooled dictionary. Read it
        // from a flow with no ambient transaction so no pending value can mask the stored value.
        string? modelFirstName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelFirstName = person.FirstName);
        Assert.Equal("New", modelFirstName);
        Assert.DoesNotContain(openTransaction.GetPendingChanges(), change => change.Property.Name == nameof(Person.FirstName));

        await openTransaction.CommitAsync(CancellationToken.None);
        Assert.Equal("New", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task WhenAmbientTransactionIsDisposedAndAnotherIsOpen_ThenReadReturnsTheModelValueInsteadOfAPendingValue()
    {
        // Arrange: capture the flow while a transaction is current and holds a pending value, then dispose
        // that transaction. The captured flow keeps pointing at the disposed transaction, which is what any
        // thread started inside a transaction sees for its whole life (an Rx EventLoopScheduler worker,
        // for example).
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Model" };

        var disposedTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        person.FirstName = "PendingInDisposedTransaction";
        disposedTransaction.Dispose();

        // The second transaction keeps the process-wide active count above zero, so the interceptor does not
        // take its no-transaction fast path. Whether the pool hands it the dictionary the first transaction
        // returned does not matter: Dispose nulled that transaction's reference to it.
        using var openTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "PendingInOpenTransaction";

        // Act
        string? readThroughDisposedTransaction = null;
        ExecutionContext.Run(frozenFlow, _ => readThroughDisposedTransaction = person.FirstName, null);

        // Assert: neither the disposed transaction's captured value nor the open transaction's pending value.
        Assert.Equal("Model", readThroughDisposedTransaction);

        // The disposed transaction holds nothing to serve, whichever transaction owns the pooled dictionary.
        Assert.Empty(disposedTransaction.GetPendingChanges());
        Assert.False(disposedTransaction.TryGetPendingValue<string?>(
            new PropertyReference(person, nameof(Person.FirstName)), out _));

        string? modelFirstName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelFirstName = person.FirstName);
        Assert.Equal("Model", modelFirstName);

        // The open transaction still masks reads in its own flow.
        Assert.Equal("PendingInOpenTransaction", person.FirstName);
    }

    [Fact]
    public async Task WhenAmbientTransactionIsDisposedWhileAnotherThreadWrites_ThenNoExceptionEscapesTheSetterAndTheWriteLands()
    {
        // Arrange: a thread born inside a transaction keeps that transaction in its ambient slot for life
        // (an Rx EventLoopScheduler worker, for example). Writing on that thread while another flow disposes
        // the transaction races the interceptor's disposed check against the capture itself. An exception
        // escaping the setter there is unhandled on a bare thread and terminates the process, so the write
        // has to fall through to the model instead, exactly as if no transaction were ambient.
        var context = CreateTransactionContext();
        var person = new Person(context) { LastName = "Model" };

        // The race is reachable within a handful of iterations; bounded so the test always terminates.
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
            var frozenFlow = ExecutionContext.Capture()
                ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");

            var writerStarted = new ManualResetEventSlim();
            var disposeCompleted = false;
            Exception? escapedException = null;
            var valueWrittenAfterDispose = $"AfterDispose{iteration}";

            var writerThread = new Thread(() => ExecutionContext.Run(frozenFlow, _ =>
            {
                try
                {
                    writerStarted.Set();
                    var writeCount = 0;
                    while (!Volatile.Read(ref disposeCompleted))
                    {
                        // Distinct values so the equality check never short-circuits the chain.
                        person.LastName = $"Racing{writeCount++}";
                    }

                    person.LastName = valueWrittenAfterDispose;
                }
                catch (Exception exception)
                {
                    escapedException = exception;
                }
            }, null));

            // Act
            writerThread.Start();
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(10)), "writer did not start");
            transaction.Dispose();
            Volatile.Write(ref disposeCompleted, true);
            Assert.True(writerThread.Join(TimeSpan.FromSeconds(10)), "writer did not stop");

            // Assert
            Assert.Null(escapedException);
            Assert.Equal(valueWrittenAfterDispose, person.LastName);
        }
    }

    [Fact]
    public async Task WhenCommitIsParkedOnTheTransactionLockAndTheTransactionIsDisposed_ThenTheCommitCompletesWithoutFailing()
    {
        // Arrange: an optimistic commit parks acquiring the per-context transaction lock. A dispose on
        // another flow then releases the pending-changes buffer while the commit is still parked, so the
        // commit resumes with a null buffer. The null tolerance in StartCommitAndSnapshotChanges and
        // FinishCommit is what keeps that from being a NullReferenceException; it is load-bearing, not
        // defensive.
        var context = CreateTransactionContext();
        var person = new Person(context) { LastName = "Model" };

        var lockHolder = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        SubjectTransaction? parkedTransaction = null;
        Task? parkedCommit = null;
        var commitParked = new ManualResetEventSlim();

        // Suppressed so the parked transaction does not inherit lockHolder as its ambient transaction.
        var flowControl = ExecutionContext.SuppressFlow();
        Task starter;
        try
        {
            starter = Task.Run(async () =>
            {
                parkedTransaction = await context.BeginTransactionAsync(
                    TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic);
                person.LastName = "Pending";

                // CommitAsync has already parked on the lock by the time it returns its incomplete task.
                parkedCommit = parkedTransaction.CommitAsync(CancellationToken.None).AsTask();
                commitParked.Set();
                await parkedCommit;
            });
        }
        finally
        {
            flowControl.Undo();
        }

        Assert.True(commitParked.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the transaction lock");

        // Act
        parkedTransaction!.Dispose();
        lockHolder.Dispose();
        await starter.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.True(parkedCommit!.IsCompletedSuccessfully);
        Assert.Empty(parkedTransaction.GetPendingChanges());

        string? modelLastName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelLastName = person.LastName);
        Assert.Equal("Model", modelLastName);
    }

    [Fact]
    public async Task WhenSourceValueIsTransformedBeforeCapture_ThenTheCapturedOriginIsLocal()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions()
            .WithRegistry();
        context.AddService<IWriteInterceptor>(new TransformingWriteInterceptor());

        var person = new Person(context);
        var source = new object();
        var registeredProperty = person.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.LastName))!;

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        registeredProperty.SetValueFromSource(source, null, null, "smith");

        // Assert
        var pending = Assert.Single(transaction.GetPendingChanges());
        Assert.Equal("SMITH", pending.GetNewValue<string?>());
        Assert.Equal(ChangeOriginKind.Local, pending.Origin.Kind);

        await transaction.CommitAsync(CancellationToken.None);
        Assert.Equal("SMITH", person.LastName);
    }

    /// <summary>
    /// Writes nothing to any source but marks each accepted snapshot slot with a fixed source in place,
    /// emulating a custom writer fulfilling the in-place marking contract.
    /// </summary>
    private sealed class MarkingTransactionWriter(object source) : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            var span = changes.Span;
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = span[i].WithOrigin(ChangeOrigin.Confirmed(source));
            }
            return new ValueTask<SourceWriteResult>(new SourceWriteResult([], [], [], RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    [Fact]
    public async Task WhenCustomWriterMarksSnapshotInPlace_ThenApplyNotificationsCarryThatSource()
    {
        // Arrange
        var source = new object();
        var changes = new List<SubjectPropertyChange>();
        var context = CreateTransactionContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.AddService<ITransactionWriter>(new MarkingTransactionWriter(source));

        var person = new Person(context);
        changes.Clear();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert: the local apply published the FirstName change carrying the writer's source.
        var firstNameChange = Assert.Single(changes, c => c.Property.Name == nameof(Person.FirstName));
        Assert.Same(source, firstNameChange.Origin.Source);
        Assert.Equal("John", firstNameChange.GetNewValue<string?>());
    }

    /// <summary>
    /// Fulfills the writer contract but never marks any snapshot slot, like a custom writer
    /// predating the in-place marking contract.
    /// </summary>
    private sealed class NonMarkingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
            => new(new SourceWriteResult([], [], [], RevertState: null));

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    [Fact]
    public async Task WhenCustomWriterDoesNotMarkSnapshot_ThenApplyNotificationsCarryNoSource()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = CreateTransactionContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.AddService<ITransactionWriter>(new NonMarkingTransactionWriter());

        var person = new Person(context);
        changes.Clear();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert: the commit succeeds, but unmarked slots publish with no source, so an outbound
        // connector queue would not recognize the notification as an echo and would push the value
        // to the source a second time (the documented graceful degradation for non-marking writers).
        var firstNameChange = Assert.Single(changes, c => c.Property.Name == nameof(Person.FirstName));
        Assert.Null(firstNameChange.Origin.Source);
        Assert.Equal("John", firstNameChange.GetNewValue<string?>());
        Assert.Equal("John", person.FirstName);
    }

}
