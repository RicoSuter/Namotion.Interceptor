using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionHandlerTests
{
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
        var context = CreateContext(out var handler);
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
        var context = CreateContext(out var handler);
        handler.RejectCapture = (_, value) => Equals(value, "Bad") ? new InvalidOperationException("bad value") : null;
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "Bad");
        Assert.Equal("bad value", exception.Message);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public void WhenWriteIsOutsideTransaction_ThenOnCapturingIsNotCalled()
    {
        // Arrange
        var context = CreateContext(out var handler);
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
        var context = CreateContext(out var handler);
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
        var context = CreateContext(out var handler);
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

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task WhenCheckedChangeCarriesTimestamps_ThenChangeContextDuringCheckCarriesNone(bool withWriter, int expectedChecks)
    {
        // Arrange
        var changedTimestamp = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var receivedTimestamp = changedTimestamp.AddSeconds(1);
        var context = CreateContext(out var handler, withWriter ? new CountingTransactionWriter() : null);
        var person = new Person(context);
        var changeContextsSeen = new List<SubjectChangeContext>();
        var changesSeen = new List<SubjectPropertyChange>();
        handler.OnCommittingAction = (_, changes) =>
        {
            changeContextsSeen.Add(SubjectChangeContext.Current);
            changesSeen.Add(Assert.Single(changes));
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        new PropertyReference(person, nameof(Person.FirstName))
            .SetValueFromSource(new object(), changedTimestamp, receivedTimestamp, "Bob");
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedChecks, changeContextsSeen.Count);
        Assert.All(changesSeen, change =>
        {
            Assert.Equal(changedTimestamp, change.ChangedTimestamp);
            Assert.Equal(receivedTimestamp, change.ReceivedTimestamp);
        });
        Assert.All(changeContextsSeen, changeContext =>
        {
            Assert.Equal(default, changeContext);
            Assert.Null(changeContext.ReceivedTimestamp);
        });
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenOnCommittingThrows_ThenNothingIsAppliedAndRetryCommits()
    {
        // Arrange
        var reject = true;
        var context = CreateContext(out var handler);
        handler.OnCommittingAction = (_, _) =>
        {
            if (reject)
            {
                throw new InvalidOperationException("rejected");
            }
        };
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
        var context = CreateContext(out var handler);
        var person = new Person(context);
        Exception? writeError = null;
        handler.OnCommittingAction = (_, _) => writeError = Record.Exception(() => person.LastName = "Smith");

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(writeError);
        Assert.Contains("Transaction handlers must not write properties", exception.Message);
        Assert.DoesNotContain(handler.Captured, captured => captured.Property.Name == nameof(Person.LastName));
        Assert.Equal("Bob", person.FirstName);
        Assert.Null(person.LastName);
    }

    [Fact]
    public async Task WhenHandlerWritesSettableDerivedPropertyDuringOnCommitting_ThenWriteThrows()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var subject = new TransactionCascadeSubject(context);
        Exception? writeError = null;
        handler.OnCommittingAction = (_, _) => writeError = Record.Exception(() => subject.DerivedWithSetter = "derived");

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "plain";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(writeError);
        Assert.Contains("Transaction handlers must not write properties", exception.Message);
        Assert.Null(subject.DerivedWithSetter);
        Assert.Equal("plain", subject.Plain);
    }

    [Fact]
    public async Task WhenHandlerWritesLandedValueOfPendingPropertyDuringOnCommitting_ThenWriteIsDropped()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var person = new Person(context) { FirstName = "Ann" };
        Exception? writeError = null;
        handler.OnCommittingAction = (_, _) => writeError = Record.Exception(() => person.FirstName = "Ann");

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Null(writeError);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenAnotherThreadReadsDuringOnCommitting_ThenReadThrows()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var person = new Person(context) { FirstName = "Ann" };
        Exception? concurrentReadException = null;
        handler.OnCommittingAction = (_, _) =>
        {
            // A started thread inherits the execution context, so it carries the transaction.
            var thread = new Thread(() => concurrentReadException = Record.Exception(() => _ = person.FirstName));
            thread.Start();
            thread.Join();
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(concurrentReadException);
        Assert.Contains("commit is in progress", exception.Message);
        Assert.Equal("Bob", person.FirstName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAnotherThreadWritesLandedValueOfPendingPropertyDuringCommit_ThenWriteThrows(bool duringSourceWrite)
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, duringSourceWrite ? writer : null);
        var person = new Person(context) { FirstName = "Ann" };
        Exception? writeError = null;
        void WriteBackFromAnotherThread()
        {
            // A started thread inherits the execution context, so it carries the transaction.
            var thread = new Thread(() => writeError = Record.Exception(() => person.FirstName = "Ann"));
            thread.Start();
            thread.Join();
        }

        if (duringSourceWrite)
        {
            writer.DuringWrite = WriteBackFromAnotherThread;
        }
        else
        {
            handler.OnCommittingAction = (_, _) => WriteBackFromAnotherThread();
        }

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(writeError);
        Assert.Contains("commit is in progress", exception.Message);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenWriterFlowWritesLandedValueOfPendingPropertyDuringSourceWrite_ThenWriteThrows()
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out _, writer);
        var person = new Person(context) { FirstName = "Ann" };
        Exception? writeError = null;
        writer.DuringWrite = () => writeError = Record.Exception(() => person.FirstName = "Ann");

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(writeError);
        Assert.Contains("commit is in progress", exception.Message);
        Assert.Equal("Bob", person.FirstName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenChangeHookWritesLandedValueOfPendingPropertyDuringApply_ThenWriteIsDropped(bool withWriter)
    {
        // Arrange
        var context = CreateContext(out _, withWriter ? new CountingTransactionWriter() : null);
        var subject = new TransactionCascadeSubject(context) { Plain = "plain", SideEffect = "landed" };
        var sideEffectChanges = new List<(string? OldValue, string? NewValue)>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.SideEffect))
            .Subscribe(change => sideEffectChanges.Add((change.GetOldValue<string?>(), change.GetNewValue<string?>())));
        Exception? writeError = null;

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "applied";
        subject.SideEffect = "pending";
        subject.PlainChangedAction = changed => writeError = Record.Exception(() => changed.SideEffect = "landed");
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Null(writeError);
        Assert.Equal("applied", subject.Plain);
        Assert.Equal("pending", subject.SideEffect);
        Assert.Equal([("landed", "pending")], sideEffectChanges);
    }

    [Fact]
    public async Task WhenApplyFailsAfterHandlersCheckedTheSet_ThenLaterChangesAreNotApplied()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var probe = new CommitWriteProbe(handler) { ThrowOnConsumedProperty = nameof(Person.FirstName) };
        context.AddService(probe);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        person.LastName = "Kim";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal([nameof(Person.FirstName)], probe.Writes.Select(write => write.Property));
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName)],
            exception.FailedChanges.Select(change => change.Property.Name));
        Assert.Equal("apply failed", Assert.Single(exception.Errors).Message);
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Lee", person.LastName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenLocalRevertFailsAfterHandlersCheckedTheSet_ThenStuckChangeIsReportedAgain(bool withWriter)
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out _, withWriter ? writer : null);
        var rejectingInterceptor = new ValueRejectingWriteInterceptor();
        context.AddService(rejectingInterceptor);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        rejectingInterceptor.Rejected.Add((nameof(Person.LastName), "Kim"));
        rejectingInterceptor.Rejected.Add((nameof(Person.FirstName), "Ann"));

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        person.LastName = "Kim";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        // Every change fails, and FirstName, whose restore failed, is listed again as stuck.
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName), nameof(Person.FirstName)],
            exception.FailedChanges.Select(change => change.Property.Name));
        Assert.Equal(
            ["LastName write failed.", "FirstName write failed."],
            exception.Errors.Select(error => error.Message));
        Assert.Contains("could not be reverted", exception.Message);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal("Bob", person.FirstName);
        Assert.Equal("Lee", person.LastName);
        Assert.Equal(
            withWriter ? [nameof(Person.FirstName), nameof(Person.LastName)] : [],
            writer.Reverted.Select(change => change.Property.Name));
    }

    [Fact]
    public async Task WhenSourceWriteAndApplyFailAfterHandlersCheckedTheSet_ThenNothingLandsAndOnlyWrittenSourcesAreReverted()
    {
        // Arrange
        var writer = new CountingTransactionWriter { FailedPropertyNames = { nameof(TransactionCascadeSubject.SideEffect) } };
        var context = CreateContext(out _, writer);
        var subject = new TransactionCascadeSubject(context) { Plain = "plain", Failing = "failing", SideEffect = "side" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "new plain";
        subject.Failing = "new failing";
        subject.SideEffect = "new side";
        subject.ThrowOnFailingWrite = true;
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("plain", subject.Plain);
        Assert.Equal("failing", subject.Failing);
        Assert.Equal("side", subject.SideEffect);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(
            [nameof(TransactionCascadeSubject.SideEffect), nameof(TransactionCascadeSubject.Plain), nameof(TransactionCascadeSubject.Failing)],
            exception.FailedChanges.Select(change => change.Property.Name));
        Assert.Equal(
            [nameof(TransactionCascadeSubject.Plain), nameof(TransactionCascadeSubject.Failing)],
            writer.Reverted.Select(change => change.Property.Name));
        Assert.Equal(["source write failed", "Setter failed."], exception.Errors.Select(error => error.Message));
    }

    [Fact]
    public async Task WhenDisposedDuringOnCommitting_ThenLockIsReleasedOnlyAfterCommitEnds()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var person = new Person(context) { FirstName = "Ann" };
        Task<SubjectTransaction>? competingBegin = null;
        var competingBeginCompletedDuringCommit = true;
        handler.OnCommittingAction = (committingTransaction, _) =>
        {
            committingTransaction.Dispose();

            // Started inline so that it completes synchronously if disposal released the exclusive lock.
            competingBegin = BeginInOwnFlowAsync(context);
            competingBeginCompletedDuringCommit = competingBegin.IsCompleted;
        };
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
    public async Task WhenDisposedDuringOnCommittingAndAgainAfterCommit_ThenLockIsReleasedOnce()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var person = new Person(context) { FirstName = "Ann" };
        handler.OnCommittingAction = (committingTransaction, _) => committingTransaction.Dispose();
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        transaction.Dispose();
        var firstBegin = BeginInOwnFlowAsync(context);
        var firstBeginCompletedSynchronously = firstBegin.IsCompletedSuccessfully;
        var secondBegin = BeginInOwnFlowAsync(context);
        var secondBeginCompletedWhileFirstHeld = secondBegin.IsCompleted;
        (await firstBegin).Dispose();
        (await secondBegin).Dispose();

        // Assert
        Assert.True(firstBeginCompletedSynchronously);
        Assert.False(secondBeginCompletedWhileFirstHeld);
    }

    [Fact]
    public async Task WhenResolvingHandlersFailsOnBegin_ThenExclusiveLockIsNotHeld()
    {
        // Arrange
        var context = CreateContext();
        var handlerContext = InterceptorSubjectContext.Create();
        handlerContext.AddService<ITransactionHandler>(new MisorderedHandler());
        context.AddFallbackContext(handlerContext);
        var beginException = await Record.ExceptionAsync(() => BeginInOwnFlowAsync(context));
        context.RemoveFallbackContext(handlerContext);

        // Act
        // Started inline so that it completes synchronously only if the failed begin left the lock free.
        var competingBegin = BeginInOwnFlowAsync(context);

        // Assert
        Assert.Contains("[RunsFirst] and [RunsLast]", Assert.IsType<InvalidOperationException>(beginException).Message);
        Assert.True(competingBegin.IsCompletedSuccessfully);
        (await competingBegin).Dispose();
    }

    [Fact]
    public async Task WhenOnCommittingThrows_ThenWriterIsNotCalled()
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, writer);
        handler.OnCommittingAction = (_, _) => throw new InvalidOperationException("rejected");
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
    public async Task WhenTwoHandlersAreRegistered_ThenOnCommittingRunsInOrderAndFirstThrowStopsTheRest()
    {
        // Arrange
        var reject = true;
        var committingLog = new List<RecordingHandler>();
        var first = new RecordingHandler();
        var second = new RecordingHandler();
        first.OnCommittingAction = (_, _) =>
        {
            committingLog.Add(first);
            if (reject)
            {
                throw new InvalidOperationException("rejected");
            }
        };
        second.OnCommittingAction = (_, _) => committingLog.Add(second);
        var context = CreateContext();
        context.AddService<ITransactionHandler>(first);
        context.AddService<ITransactionHandler>(second);
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        var secondCallsAfterRejection = second.CommittingCalls;
        reject = false;
        committingLog.Clear();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, secondCallsAfterRejection);
        Assert.Equal([first, second], committingLog);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenOnCommittingThrowsWithOptimisticLocking_ThenCommitLockIsReleased()
    {
        // Arrange
        var reject = true;
        var context = CreateContext(out var handler);
        handler.OnCommittingAction = (_, _) =>
        {
            if (reject)
            {
                throw new InvalidOperationException("rejected");
            }
        };
        var person = new Person(context) { FirstName = "Ann" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic);
        person.FirstName = "Bob";
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        reject = false;

        // Act
        // Started inline so that it completes synchronously only if the failed commit released the lock.
        Task? competingCommit = null;
        RunOutsideTransactionFlow(() => competingCommit = CommitInOwnFlowAsync(context, person));

        // Assert
        Assert.True(competingCommit!.IsCompletedSuccessfully);
        Assert.Equal("Lee", person.LastName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenCommitAppliesCheckedValue_ThenCommitWriteIsConsumedOnce(bool withWriter)
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, withWriter ? writer : null);
        var probe = new CommitWriteProbe(handler);
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
        Assert.Equal(withWriter ? 1 : 0, writer.WriteCalls);
    }

    [Fact]
    public async Task WhenHandlerDidNotCheckTheCommit_ThenCommitWriteIsNotConsumed()
    {
        // Arrange
        var context = CreateContext(out _);
        var otherHandler = new RecordingHandler();
        var probe = new CommitWriteProbe(otherHandler);
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
        Assert.Equal("Bob", person.FirstName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenRollbackRestoresValue_ThenRestoreIsConsumedAsCommitWrite(bool withWriter)
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, withWriter ? writer : null);
        var probe = new CommitWriteProbe(handler) { ThrowOnConsumedProperty = nameof(Person.LastName) };
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
        Assert.Equal(withWriter ? 1 : 0, writer.WriteCalls);
    }

    private static async Task<SubjectTransaction> BeginInOwnFlowAsync(IInterceptorSubjectContext context)
    {
        // An async method does not leak the transaction it begins into the caller's flow.
        return await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
    }

    private static async Task CommitInOwnFlowAsync(IInterceptorSubjectContext context, Person person)
    {
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic);
        person.LastName = "Lee";
        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>
    /// Declares contradicting ordering attributes, so resolving the transaction handlers throws while it is registered.
    /// </summary>
    [RunsFirst]
    [RunsLast]
    private sealed class MisorderedHandler : ITransactionHandler
    {
        public void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin)
        {
        }

        public void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes)
        {
        }
    }

    /// <summary>
    /// Throws for every write of a configured value to the property of that name. Runs after the transaction
    /// interceptor, so it sees only the writes that reach the model.
    /// </summary>
    [RunsAfter(typeof(SubjectTransactionInterceptor))]
    private sealed class ValueRejectingWriteInterceptor : IWriteInterceptor
    {
        public HashSet<(string Property, object? Value)> Rejected { get; } = [];

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (Rejected.Contains((context.Property.Name, context.NewValue)))
            {
                throw new InvalidOperationException($"{context.Property.Name} write failed.");
            }

            next(ref context);
        }
    }
}
