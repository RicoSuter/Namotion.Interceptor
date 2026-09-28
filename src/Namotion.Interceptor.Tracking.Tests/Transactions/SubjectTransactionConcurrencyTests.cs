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

[InterceptorSubject]
internal partial class DisposalCaptureGateSubject
{
    public partial DisposalCaptureGateValue? Value { get; set; }
}

internal sealed class DisposalCaptureGateValue(
    string value,
    ManualResetEventSlim? comparisonEntered = null,
    ManualResetEventSlim? continueComparison = null) : IEquatable<DisposalCaptureGateValue>
{
    private readonly string _value = value;

    public bool Equals(DisposalCaptureGateValue? other)
    {
        if (comparisonEntered is not null)
        {
            comparisonEntered.Set();
            Assert.True(
                continueComparison!.Wait(TimeSpan.FromSeconds(10)),
                "The test did not release the origin comparison within 10 seconds.");
        }

        return other is not null && _value == other._value;
    }

    public override bool Equals(object? obj) => obj is DisposalCaptureGateValue other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);
}

public class SubjectTransactionConcurrencyTests
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
    public async Task WhenAmbientTransactionIsDisposedAndAnotherIsOpen_ThenDerivedPropertiesStillRecalculate()
    {
        // Arrange: same frozen-flow setup as the write test above. The write reaches the model, so its
        // derived cascade has to run with it: the disposed transaction has no commit left to replay it on,
        // so suppressing the cascade would drop the recalculation forever.
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Old", LastName = "Doe" };

        var disposedTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        disposedTransaction.Dispose();

        // Keeps the process-wide active count above zero, so the derived handler does not take its
        // no-transaction fast path.
        using var openTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        var changedProperties = new List<string>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changedProperties.Add(change.Property.Name));

        // Act
        ExecutionContext.Run(frozenFlow, _ => person.FirstName = "New", null);

        // Assert
        Assert.Contains(nameof(Person.FirstName), changedProperties);
        Assert.Contains(nameof(Person.FullName), changedProperties);
        Assert.Contains(nameof(Person.FullNameWithPrefix), changedProperties);
    }

    [Fact]
    public async Task WhenAmbientTransactionIsOpen_ThenWriteIsCapturedInsteadOfAppliedToModel()
    {
        // Arrange
        var context = CreateTransactionContext();
        var person = new Person(context) { FirstName = "Old" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "New";

        // Assert
        var pendingChange = Assert.Single(transaction.GetPendingChanges());
        Assert.Equal(nameof(Person.FirstName), pendingChange.Property.Name);
        Assert.Equal("New", pendingChange.GetNewValue<string?>());

        string? modelFirstName = null;
        await RunWithoutAsyncLocalFlowAsync(() => modelFirstName = person.FirstName);
        Assert.Equal("Old", modelFirstName);
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
    public async Task WhenDisposeWinsAfterThePublicWritePassedItsAmbientCheck_ThenTheWriteLandsWithoutAnException()
    {
        // Arrange: final-origin resolution invokes user equality immediately before TryCaptureChange. Park
        // there so Dispose deterministically wins after the interceptor's lock-free ambient-state check.
        // Regression mutation: throwing instead of returning false from TryCaptureChange when disposal wins
        // lets ObjectDisposedException escape this public write path instead of falling through to the model.
        var context = CreateTransactionContext();
        var subject = new DisposalCaptureGateSubject(context)
        {
            Value = new DisposalCaptureGateValue("Model")
        };
        var property = new PropertyReference(subject, nameof(DisposalCaptureGateSubject.Value));
        using var comparisonEntered = new ManualResetEventSlim();
        using var continueComparison = new ManualResetEventSlim();
        var sentValue = new DisposalCaptureGateValue("AfterDispose", comparisonEntered, continueComparison);
        var valueWrittenAfterDispose = new DisposalCaptureGateValue("AfterDispose");

        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        Exception? escapedException = null;
        var writerThread = new Thread(() => ExecutionContext.Run(frozenFlow, _ =>
        {
            try
            {
                property.SetValueFromOrigin(
                    ChangeOrigin.FromSource(new object()),
                    changedTimestamp: null,
                    receivedTimestamp: null,
                    value: valueWrittenAfterDispose,
                    sentValue: sentValue);
            }
            catch (Exception exception)
            {
                escapedException = exception;
            }
        }, null));

        // Act
        bool writerStopped;
        try
        {
            writerThread.Start();
            Assert.True(comparisonEntered.Wait(TimeSpan.FromSeconds(10)), "write did not reach origin comparison");
            transaction.Dispose();
        }
        finally
        {
            transaction.Dispose();
            continueComparison.Set();

            // Only capture here: an assertion inside the finally would replace a failure from the try body,
            // and a writer parked elsewhere never stops, so the join failure would hide the real one.
            writerStopped = writerThread.Join(TimeSpan.FromSeconds(10));
        }

        // Assert
        Assert.True(writerStopped, "writer did not stop");
        Assert.Null(escapedException);
        Assert.Same(valueWrittenAfterDispose, subject.Value);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenAmbientTransactionWasDisposedByAnotherFlow_ThenTheFrozenFlowCanBeginANewTransaction()
    {
        // Arrange: freeze a flow while a transaction is current, then dispose that transaction from the flow
        // that owns it. Dispose clears the ambient slot only for the disposing flow, so the frozen flow keeps
        // pointing at the disposed transaction for the rest of its life.
        var context = CreateTransactionContext();
        var disposedTransaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        var frozenFlow = ExecutionContext.Capture()
            ?? throw new InvalidOperationException("Execution context flow must not be suppressed in this test.");
        disposedTransaction.Dispose();

        // Act
        SubjectTransaction? newTransaction = null;
        try
        {
            Exception? failure = null;
            ExecutionContext.Run(frozenFlow, _ =>
            {
                Assert.Same(disposedTransaction, SubjectTransaction.Current);
                try
                {
                    newTransaction = context
                        .BeginTransactionAsync(TransactionFailureHandling.BestEffort)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }, null);

            // Assert
            Assert.Null(failure);
            Assert.NotNull(newTransaction);
        }
        finally
        {
            newTransaction?.Dispose();
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
    public async Task WhenTransactionIsDisposedDuringCommit_ThenTryCaptureChangeReturnsFalse()
    {
        // Arrange
        var writer = new ControllableTransactionWriter();
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { LastName = "Model" };
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Pending";
        var commitTask = transaction.CommitAsync(CancellationToken.None).AsTask();
        bool TryCaptureChange() => transaction.TryCaptureChange(
            new PropertyReference(person, nameof(Person.LastName)),
            ChangeOrigin.Local,
            DateTimeOffset.UnixEpoch,
            receivedTimestamp: null,
            currentValue: "Model",
            newValue: "AfterDispose");

        try
        {
            Assert.True(writer.CommitStarted.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the writer");
            Assert.Throws<InvalidOperationException>(() => TryCaptureChange());
            transaction.Dispose();

            // Act
            var captured = TryCaptureChange();

            // Assert
            Assert.False(captured);
        }
        finally
        {
            writer.Release();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task WhenDisposedDuringExternalWriterCommit_ThenRawWriteLandsBeforeFrozenReplayOverwritesIt()
    {
        // Arrange
        var writer = new ControllableTransactionWriter();
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Model" };
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "FrozenReplay";
        var commitTask = transaction.CommitAsync(CancellationToken.None).AsTask();

        try
        {
            Assert.True(writer.CommitStarted.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the writer");
            transaction.Dispose();
            Assert.Null(SubjectTransaction.Current);

            // Act: disposal makes the ambient transaction inactive even though its frozen commit continues.
            person.FirstName = "RawAfterDispose";

            // Assert: observe the landed model outside any ambient transaction before replay is released.
            string? intermediateModelValue = null;
            await RunWithoutAsyncLocalFlowAsync(() => intermediateModelValue = person.FirstName);
            Assert.Equal("RawAfterDispose", intermediateModelValue);
        }
        finally
        {
            transaction.Dispose();
            writer.Release();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Assert: transaction disposal is not isolation from an already-frozen commit snapshot.
        Assert.Equal("FrozenReplay", person.FirstName);
    }

    [Fact]
    public async Task WhenCommitIsBlockedInWriter_ThenCallerAccessCannotEscapeTheSnapshot()
    {
        // Arrange
        var writer = new ControllableTransactionWriter();
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(writer);
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "model",
            SideEffect = "side-model",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;
        var sideEffectSubject = new SideEffectWritePerson(context) { Name = "before" };
        var sideEffectBeforeCommit = sideEffectSubject.SideEffectTarget;

        var derivedChanges = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(derivedChanges.Add);

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "snapshot";
        sideEffectSubject.Name = "committed-name";
        var commitTask = transaction.CommitAsync(CancellationToken.None).AsTask();

        try
        {
            Assert.True(writer.CommitStarted.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the writer");

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => _ = subject.Plain);
            Assert.Throws<InvalidOperationException>(() => subject.SideEffect = "outside-snapshot");
            Assert.Throws<InvalidOperationException>(() => subject.DerivedWithSetter = "outside-snapshot");

            var frozenChanges = transaction.GetPendingChanges();
            Assert.Equal(2, frozenChanges.Count);
            Assert.Contains(frozenChanges, change =>
                change.Property.Name == nameof(TransactionCascadeSubject.Plain) &&
                change.GetNewValue<string>() == "snapshot");
            Assert.Contains(frozenChanges, change =>
                change.Property.Name == nameof(SideEffectWritePerson.Name) &&
                change.GetNewValue<string>() == "committed-name");

            string? modelPlain = null;
            string? modelSideEffect = null;
            string? modelDerivedWithSetter = null;
            await RunWithoutAsyncLocalFlowAsync(() =>
            {
                modelPlain = subject.Plain;
                modelSideEffect = subject.SideEffect;
                modelDerivedWithSetter = subject.DerivedWithSetter;
            });
            Assert.Equal("model", modelPlain);
            Assert.Equal("side-model", modelSideEffect);
            Assert.Equal("d0", modelDerivedWithSetter);
        }
        finally
        {
            writer.Release();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal("snapshot", subject.Plain);
        Assert.Equal("side-model", subject.SideEffect);
        Assert.Equal("d0", subject.DerivedWithSetter);
        Assert.Equal("[snapshot|d0]", subject.CombinedAgain);
        Assert.Equal("committed-name", sideEffectSubject.Name);
        Assert.NotEqual(sideEffectBeforeCommit, sideEffectSubject.SideEffectTarget);
        Assert.Contains(derivedChanges, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetNewValue<string>() == "snapshot|d0");
        Assert.Contains(derivedChanges, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.CombinedAgain) &&
            change.GetNewValue<string>() == "[snapshot|d0]");
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenCommitIsBlockedInWriter_ThenForkedAmbientAccessIsRejected()
    {
        // Arrange
        var writer = new ControllableTransactionWriter();
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(writer);
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "model",
            SideEffect = "side-model",
            DerivedWithSetter = "d0"
        };

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "snapshot";
        var commitTask = transaction.CommitAsync(CancellationToken.None).AsTask();

        try
        {
            Assert.True(writer.CommitStarted.Wait(TimeSpan.FromSeconds(10)), "commit did not reach the writer");

            // Act
            var accessTask = Task.Run(() =>
            {
                var readException = CaptureException(() => _ = subject.Plain);
                var writeException = CaptureException(() => subject.SideEffect = "forked-outside-snapshot");
                var derivedWriteException = CaptureException(
                    () => subject.DerivedWithSetter = "forked-outside-snapshot");
                return (readException, writeException, derivedWriteException);
            });
            var exceptions = await accessTask.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.IsType<InvalidOperationException>(exceptions.readException);
            Assert.IsType<InvalidOperationException>(exceptions.writeException);
            Assert.IsType<InvalidOperationException>(exceptions.derivedWriteException);

            var frozenChange = Assert.Single(transaction.GetPendingChanges());
            Assert.Equal(nameof(TransactionCascadeSubject.Plain), frozenChange.Property.Name);
            Assert.Equal("snapshot", frozenChange.GetNewValue<string>());

            string? modelPlain = null;
            string? modelSideEffect = null;
            string? modelDerivedWithSetter = null;
            await RunWithoutAsyncLocalFlowAsync(() =>
            {
                modelPlain = subject.Plain;
                modelSideEffect = subject.SideEffect;
                modelDerivedWithSetter = subject.DerivedWithSetter;
            });
            Assert.Equal("model", modelPlain);
            Assert.Equal("side-model", modelSideEffect);
            Assert.Equal("d0", modelDerivedWithSetter);
        }
        finally
        {
            writer.Release();
            await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal("snapshot", subject.Plain);
        Assert.Equal("side-model", subject.SideEffect);
        Assert.Equal("d0", subject.DerivedWithSetter);
    }

    [Fact]
    public async Task WhenWriterAccessesAmbientPropertyDuringCommit_ThenCommitReportsConcurrentAccess()
    {
        // Arrange
        var context = CreateTransactionContext();
        var subject = new TransactionCascadeSubject(context) { Plain = "model" };
        context.AddService<ITransactionWriter>(new PropertyAccessingTransactionWriter(() => _ = subject.Plain));
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "pending";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        var error = Assert.IsType<InvalidOperationException>(Assert.Single(exception.Errors));
        Assert.Contains("commit is in progress", error.Message);
        Assert.Equal("model", subject.Plain);
        Assert.Empty(transaction.GetPendingChanges());
    }

    private sealed class ControllableTransactionWriter : ITransactionWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim CommitStarted { get; } = new();

        public async ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            CommitStarted.Set();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new SourceWriteResult([], [], [], RevertState: null);
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));

        public void Release() => _release.TrySetResult();
    }

    private sealed class PropertyAccessingTransactionWriter(Action accessProperty) : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            accessProperty();
            return new ValueTask<SourceWriteResult>(new SourceWriteResult([], [], [], RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

}
