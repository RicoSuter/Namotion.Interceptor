using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionHandlerTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
    }

    [Fact]
    public async Task WhenNoValidationModeIsPassed_ThenTransactionUsesImmediate()
    {
        // Arrange
        var context = CreateContext();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Assert
        Assert.Equal(TransactionValidationMode.Immediate, transaction.ValidationMode);
    }

    [Fact]
    public async Task WhenDeferredValidationModeIsPassed_ThenTransactionExposesIt()
    {
        // Arrange
        var context = CreateContext();

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            validationMode: TransactionValidationMode.Deferred);

        // Assert
        Assert.Equal(TransactionValidationMode.Deferred, transaction.ValidationMode);
    }

    [Fact]
    public async Task WhenWriteIsCaptured_ThenOnCapturingReceivesValueAndAttemptedOrigin()
    {
        // Arrange
        var handler = new RecordingHandler();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var person = new Person(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";

        // Assert
        var captured = Assert.Single(handler.Captured);
        Assert.Equal(nameof(Person.FirstName), captured.Property.Name);
        Assert.Equal("Bob", captured.Value);
        Assert.Equal(ChangeOriginKind.Local, captured.Origin.Kind);
    }

    [Fact]
    public async Task WhenOnCapturingThrows_ThenWriteIsNotCaptured()
    {
        // Arrange
        var handler = new RecordingHandler
        {
            RejectCapture = (_, value) => Equals(value, "Bad") ? new InvalidOperationException("bad value") : null
        };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "Bad");
        Assert.Equal("bad value", exception.Message);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenWriteIsOutsideTransaction_ThenOnCapturingIsNotCalled()
    {
        // Arrange
        var handler = new RecordingHandler();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var person = new Person(context);

        // Act
        person.FirstName = "Bob";

        // Assert
        Assert.Empty(handler.Captured);
    }

    [Fact]
    public async Task WhenWriteComesFromSource_ThenOnCapturingReceivesSourceOrigin()
    {
        // Arrange
        var handler = new RecordingHandler();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var person = new Person(context);
        var source = new object();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        new PropertyReference(person, nameof(Person.FirstName)).SetValueFromSource(source, null, null, "Bob");

        // Assert
        var captured = Assert.Single(handler.Captured);
        Assert.Equal("Bob", captured.Value);
        Assert.Equal(ChangeOriginKind.FromSource, captured.Origin.Kind);
        Assert.Same(source, captured.Origin.Source);
    }

    [Fact]
    public async Task WhenTwoHandlersAreRegistered_ThenTheyRunInOrderAndFirstThrowStopsTheRest()
    {
        // Arrange
        var captureLog = new List<(RecordingHandler Handler, object? Value)>();
        var first = new RecordingHandler
        {
            CaptureLog = captureLog,
            RejectCapture = (_, value) => Equals(value, "Bad") ? new InvalidOperationException("bad value") : null
        };
        var second = new RecordingHandler { CaptureLog = captureLog };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(first);
        context.AddService<ITransactionHandler>(second);
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Act
        person.FirstName = "Good";
        var exception = Record.Exception(() => person.FirstName = "Bad");

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(2, captureLog.Count);
        Assert.Same(first, captureLog[0].Handler);
        Assert.Equal("Good", captureLog[0].Value);
        Assert.Same(second, captureLog[1].Handler);
        Assert.Equal("Good", captureLog[1].Value);
    }

    [Fact]
    public async Task WhenHandlerIsRegisteredAfterBegin_ThenItDoesNotTakePart()
    {
        // Arrange
        var handler = new RecordingHandler();
        var context = CreateContext();
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        context.AddService<ITransactionHandler>(handler);

        // Act
        person.FirstName = "Bob";

        // Assert
        Assert.Empty(handler.Captured);
    }

    [Fact]
    public async Task WhenCommitting_ThenOnCommittingSeesPendingValuesBeforeAnythingIsApplied()
    {
        // Arrange
        var handler = new RecordingHandler();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var notifications = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(notifications.Add);
        var person = new Person(context) { FirstName = "Ann" };
        notifications.Clear();

        string? firstNameSeenByHandler = null;
        var notificationsSeenByHandler = -1;
        SubjectPropertyChange[]? changesSeenByHandler = null;
        handler.OnCommittingAction = (_, changes) =>
        {
            changesSeenByHandler = changes;
            firstNameSeenByHandler = person.FirstName;
            notificationsSeenByHandler = notifications.Count;
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.CommittingCalls);
        var change = Assert.Single(changesSeenByHandler!);
        Assert.Equal(nameof(Person.FirstName), change.Property.Name);
        Assert.Equal("Ann", change.GetOldValue<string>());
        Assert.Equal("Bob", change.GetNewValue<string>());
        Assert.Equal("Bob", firstNameSeenByHandler);
        Assert.Equal(0, notificationsSeenByHandler);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenOnCommittingThrows_ThenNothingIsAppliedAndRetryCommits()
    {
        // Arrange
        var reject = true;
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, _) =>
            {
                if (reject)
                {
                    throw new InvalidOperationException("rejected");
                }
            }
        };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var notifications = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(notifications.Add);
        var person = new Person(context) { FirstName = "Ann" };
        notifications.Clear();
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        var notificationsAfterFailure = notifications.Count;
        var pendingAfterFailure = transaction.GetPendingChanges().Count;
        person.LastName = "Lee";
        var pendingBeforeRetry = transaction.GetPendingChanges().Count;
        reject = false;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("rejected", exception.Message);
        Assert.Equal(0, notificationsAfterFailure);
        Assert.Equal(1, pendingAfterFailure);
        Assert.Equal(2, pendingBeforeRetry);
        Assert.Equal("Bob", person.FirstName);
        Assert.Equal("Lee", person.LastName);
    }

    [Fact]
    public async Task WhenHandlerWritesDuringOnCommitting_ThenWriteThrowsAndIsNotCaptured()
    {
        // Arrange
        var handler = new RecordingHandler();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var person = new Person(context);
        Exception? writeError = null;
        handler.OnCommittingAction = (_, _) => writeError = Record.Exception(() => person.LastName = "Smith");

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.IsType<InvalidOperationException>(writeError);
        Assert.DoesNotContain(handler.Captured, captured => captured.Property.Name == nameof(Person.LastName));
        Assert.Equal("Bob", person.FirstName);
        Assert.Null(person.LastName);
    }

    [Fact]
    public async Task WhenDisposedDuringOnCommitting_ThenLockIsReleasedOnlyAfterCommitEnds()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann" };
        Task<SubjectTransaction>? competingBegin = null;
        var competingBeginCompletedDuringCommit = true;
        var handler = new RecordingHandler
        {
            OnCommittingAction = (transaction, _) =>
            {
                transaction.Dispose();

                // Started inline so that it completes synchronously if disposal released the exclusive lock.
                competingBegin = BeginInOwnFlowAsync(context);
                competingBeginCompletedDuringCommit = competingBegin.IsCompleted;
            }
        };
        context.AddService<ITransactionHandler>(handler);
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";

        // Act
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        using var competingTransaction = await competingBegin!;

        // Assert
        Assert.False(competingBeginCompletedDuringCommit);
        Assert.Equal("Ann", person.FirstName);
    }

    [Fact]
    public async Task WhenNoHandlerIsRegistered_ThenCommitSucceeds()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenOnCommittingThrows_ThenWriterIsNotCalled()
    {
        // Arrange
        var handler = new RecordingHandler { OnCommittingAction = (_, _) => throw new InvalidOperationException("rejected") };
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("rejected", exception.Message);
        Assert.Equal(0, writer.WriteCalls);
    }

    [Fact]
    public async Task WhenCommitAppliesCheckedValue_ThenCommitWriteIsConsumedOnce()
    {
        // Arrange
        var handler = new RecordingHandler();
        var probe = new CommitWriteProbe(handler);
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService(probe);
        var person = new Person(context) { FirstName = "Ann" };
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var applied = Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName));
        Assert.Equal("Bob", applied.Value);
        Assert.True(applied.Consumed);
        Assert.False(applied.ConsumedAgain);
        Assert.All(
            probe.Writes.Where(write => write.Property != nameof(Person.FirstName)),
            derivedWrite => Assert.False(derivedWrite.Consumed));
    }

    [Fact]
    public async Task WhenHandlerDidNotCheckTheCommit_ThenCommitWriteIsNotConsumed()
    {
        // Arrange
        var handler = new RecordingHandler();
        var otherHandler = new RecordingHandler();
        var probe = new CommitWriteProbe(otherHandler);
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService(probe);
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.False(Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName)).Consumed);
    }

    [Fact]
    public async Task WhenNoHandlerIsRegistered_ThenCommitWriteIsNotConsumed()
    {
        // Arrange
        var unregisteredHandler = new RecordingHandler();
        var probe = new CommitWriteProbe(unregisteredHandler);
        var context = CreateContext();
        context.AddService(probe);
        var person = new Person(context);
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.False(Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName)).Consumed);
    }

    [Fact]
    public async Task WhenRollbackRestoresValue_ThenRestoreIsConsumedAsCommitWrite()
    {
        // Arrange
        var handler = new RecordingHandler();
        var probe = new CommitWriteProbe(handler) { ThrowOnConsumedProperty = nameof(Person.LastName) };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService(probe);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "Bob";
        person.LastName = "Kim";
        await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("Ann", person.FirstName);
        var restore = Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName) && Equals(write.Value, "Ann"));
        Assert.True(restore.Consumed);
    }

    [Fact]
    public async Task WhenWriterCommitAppliesCheckedValue_ThenCommitWriteIsConsumedOnce()
    {
        // Arrange
        var handler = new RecordingHandler();
        var probe = new CommitWriteProbe(handler);
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var writer = new CountingTransactionWriter();
        context.AddService<ITransactionWriter>(writer);
        context.AddService(probe);
        var person = new Person(context) { FirstName = "Ann" };
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var applied = Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName));
        Assert.Equal("Bob", applied.Value);
        Assert.True(applied.Consumed);
        Assert.False(applied.ConsumedAgain);
        Assert.Equal(1, writer.WriteCalls);
    }

    [Fact]
    public async Task WhenWriterCommitRollsBack_ThenRestoreIsConsumedAsCommitWrite()
    {
        // Arrange
        var handler = new RecordingHandler();
        var probe = new CommitWriteProbe(handler) { ThrowOnConsumedProperty = nameof(Person.LastName) };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var writer = new CountingTransactionWriter();
        context.AddService<ITransactionWriter>(writer);
        context.AddService(probe);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "Bob";
        person.LastName = "Kim";
        await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("Ann", person.FirstName);
        var restore = Assert.Single(probe.Writes, write => write.Property == nameof(Person.FirstName) && Equals(write.Value, "Ann"));
        Assert.True(restore.Consumed);
        Assert.Equal(1, writer.WriteCalls);
    }

    private static async Task<SubjectTransaction> BeginInOwnFlowAsync(IInterceptorSubjectContext context)
    {
        // An async method does not leak the transaction it begins into the caller's flow.
        return await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
    }

    private sealed class CountingTransactionWriter : ITransactionWriter
    {
        public int WriteCalls { get; private set; }

        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            return new(new SourceWriteResult(changes.ToArray(), [], [], RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
    }

    private sealed class RecordingHandler : ITransactionHandler
    {
        public List<(PropertyReference Property, object? Value, ChangeOrigin Origin)> Captured { get; } = [];

        public Func<PropertyReference, object?, Exception?>? RejectCapture { get; set; }

        public List<(RecordingHandler Handler, object? Value)>? CaptureLog { get; init; }

        public int CommittingCalls { get; private set; }

        public Action<SubjectTransaction, SubjectPropertyChange[]>? OnCommittingAction { get; set; }

        public void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin)
        {
            if (RejectCapture?.Invoke(property, newValue) is { } exception)
            {
                throw exception;
            }

            Captured.Add((property, newValue, origin));
            CaptureLog?.Add((this, newValue));
        }

        public void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes)
        {
            CommittingCalls++;
            OnCommittingAction?.Invoke(transaction, changes.ToArray());
        }
    }

    [RunsAfter(typeof(SubjectTransactionInterceptor))]
    private sealed class CommitWriteProbe(ITransactionHandler handler) : IWriteInterceptor
    {
        public List<(string Property, object? Value, bool Consumed, bool ConsumedAgain)> Writes { get; } = [];

        public string? ThrowOnConsumedProperty { get; set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            var consumed = SubjectTransaction.TryConsumeCommitWrite(handler, context.Property, context.NewValue);
            var consumedAgain = SubjectTransaction.TryConsumeCommitWrite(handler, context.Property, context.NewValue);
            Writes.Add((context.Property.Name, context.NewValue, consumed, consumedAgain));

            if (consumed && context.Property.Name == ThrowOnConsumedProperty)
            {
                throw new InvalidOperationException("apply failed");
            }

            next(ref context);
        }
    }
}
