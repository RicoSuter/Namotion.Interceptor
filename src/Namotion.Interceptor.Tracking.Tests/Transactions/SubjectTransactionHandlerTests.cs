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
        var person = new Person(context) { FirstName = "Ann" };
        var flowWithoutTransaction = ExecutionContext.Capture()!;
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, TransactionLocking.Optimistic);
        person.FirstName = "Bob";
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        reject = false;

        // Act
        // Started inline so that it completes synchronously only if the failed commit released the lock.
        Task? competingCommit = null;
        ExecutionContext.Run(flowWithoutTransaction, _ => competingCommit = CommitInOwnFlowAsync(context, person), null);

        // Assert
        Assert.True(competingCommit!.IsCompletedSuccessfully);
        Assert.Equal("Lee", person.LastName);
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

    [Fact]
    public async Task WhenWriterIsRegistered_ThenOnCommittingRunsAgainBeforeLocalApplyWithPendingValues()
    {
        // Arrange
        var handler = new RecordingHandler();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(new CountingTransactionWriter());
        var person = new Person(context) { FirstName = "Ann" };
        var seen = new List<string?>();
        handler.OnCommittingAction = (_, _) => seen.Add(person.FirstName);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, handler.CommittingCalls);
        Assert.Equal(["Bob", "Bob"], seen);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenNoWriterIsRegistered_ThenOnCommittingRunsOnce()
    {
        // Arrange
        var handler = new RecordingHandler();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var person = new Person(context) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenPropertyChangesOutsideTransactionDuringSourceWrite_ThenSecondCheckSeesLandedValue()
    {
        // Arrange
        var handler = new RecordingHandler();
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        var lastNamesSeen = new List<string?>();
        handler.OnCommittingAction = (_, _) => lastNamesSeen.Add(person.LastName);
        var flowWithoutTransaction = ExecutionContext.Capture()!;
        writer.DuringWrite = () => ExecutionContext.Run(flowWithoutTransaction, _ => person.LastName = "Kim", null);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Lee", "Kim"], lastNamesSeen);
        Assert.Equal("Bob", person.FirstName);
        Assert.Equal("Kim", person.LastName);
    }

    [Fact]
    public async Task WhenHandlerWritesDuringSecondCheck_ThenAllChangesFail()
    {
        // Arrange
        var calls = 0;
        var handler = new RecordingHandler();
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann" };
        handler.OnCommittingAction = (_, _) =>
        {
            if (++calls == 2)
            {
                person.LastName = "Smith";
            }
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("Ann", person.FirstName);
        Assert.Null(person.LastName);
        Assert.Single(exception.FailedChanges);
        var error = Assert.IsType<InvalidOperationException>(Assert.Single(exception.Errors));
        Assert.Contains("Transaction handlers must not write properties", error.Message);
        Assert.Single(writer.Reverted);
    }

    [Theory]
    [InlineData(TransactionFailureHandling.BestEffort)]
    [InlineData(TransactionFailureHandling.Rollback)]
    public async Task WhenSecondCheckRejectsChange_ThenFailureHandlingAppliesAndTransactionIsTerminal(TransactionFailureHandling failureHandling)
    {
        // Arrange
        var calls = 0;
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, changes) =>
            {
                if (++calls == 2)
                {
                    var lastName = changes.Single(change => change.Property.Name == nameof(Person.LastName));
                    throw new SubjectTransactionException(
                        "invalid", [], [lastName], [new InvalidOperationException("invalid last name")]);
                }
            }
        };
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(failureHandling);
        person.FirstName = "Bob";
        person.LastName = "Kim";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Contains(exception.FailedChanges, change => change.Property.Name == nameof(Person.LastName));
        Assert.Equal("Lee", person.LastName);
        if (failureHandling == TransactionFailureHandling.Rollback)
        {
            Assert.Equal("Ann", person.FirstName);
            Assert.Equal(2, writer.Reverted.Count);
        }
        else
        {
            Assert.Equal("Bob", person.FirstName);
            Assert.Equal(nameof(Person.LastName), Assert.Single(writer.Reverted).Property.Name);
        }

        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transaction.CommitAsync(CancellationToken.None));
        Assert.Contains("already been committed", retryException.Message);
    }

    [Fact]
    public async Task WhenSecondCheckRejectsChangeAndAnotherApplyFails_ThenEachWrittenSourceIsRevertedOnce()
    {
        // Arrange
        var calls = 0;
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, changes) =>
            {
                if (++calls == 2)
                {
                    var firstName = changes.Single(change => change.Property.Name == nameof(Person.FirstName));
                    throw new SubjectTransactionException(
                        "invalid", [], [firstName], [new InvalidOperationException("invalid first name")]);
                }
            }
        };
        var probe = new CommitWriteProbe(handler) { ThrowOnConsumedProperty = nameof(Person.LastName) };
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        context.AddService(probe);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        person.LastName = "Kim";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Lee", person.LastName);
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName)],
            exception.FailedChanges.Select(change => change.Property.Name).Order());
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName)],
            writer.Reverted.Select(change => change.Property.Name).Order());
    }

    [Fact]
    public async Task WhenSourceWriteFails_ThenSecondCheckSeesOnlyLandingChangesAndLandedValueOfFailedProperty()
    {
        // Arrange
        var person = default(Person)!;
        var calls = new List<(string[] Changes, string? FirstName, string? LastName)>();
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, changes) =>
            {
                calls.Add((changes.Select(change => change.Property.Name).ToArray(), person.FirstName, person.LastName));
                if (person.FirstName == person.LastName)
                {
                    throw new InvalidOperationException("first and last name must differ");
                }
            }
        };
        var writer = new CountingTransactionWriter { FailedPropertyNames = { nameof(Person.LastName) } };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Lee";
        person.LastName = "Ann";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(2, calls.Count);
        Assert.Equal([nameof(Person.FirstName), nameof(Person.LastName)], calls[0].Changes);
        Assert.Equal(("Lee", "Ann"), (calls[0].FirstName, calls[0].LastName));
        Assert.Equal([nameof(Person.FirstName)], calls[1].Changes);
        Assert.Equal(("Lee", "Lee"), (calls[1].FirstName, calls[1].LastName));
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Lee", person.LastName);
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName)],
            exception.FailedChanges.Select(change => change.Property.Name).Order());
        Assert.Equal(nameof(Person.FirstName), Assert.Single(writer.Reverted).Property.Name);
    }

    [Fact]
    public async Task WhenSecondCheckRejectsUncheckedChange_ThenEveryCheckedChangeFails()
    {
        // Arrange
        var calls = 0;
        var person = default(Person)!;
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, _) =>
            {
                if (++calls == 2)
                {
                    var uncheckedChange = SubjectPropertyChange.Create(
                        new PropertyReference(person, nameof(Person.LastName)),
                        ChangeOrigin.Local,
                        DateTimeOffset.UtcNow,
                        null,
                        "Lee",
                        "Kim");
                    throw new SubjectTransactionException(
                        "invalid", [], [uncheckedChange], [new InvalidOperationException("invalid last name")]);
                }
            }
        };
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Lee", person.LastName);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.IsType<SubjectTransactionException>(Assert.Single(exception.Errors));
        Assert.Equal(nameof(Person.FirstName), Assert.Single(writer.Reverted).Property.Name);
    }

    [Fact]
    public async Task WhenEverySourceWriteFails_ThenOnCommittingIsNotCalledAgainAndNothingIsApplied()
    {
        // Arrange
        var handler = new RecordingHandler();
        var writer = new CountingTransactionWriter { FailedPropertyNames = { nameof(Person.FirstName) } };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Equal("Ann", person.FirstName);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Equal("source write failed", Assert.Single(exception.Errors).Message);
        Assert.Empty(writer.Reverted);
    }

    [Fact]
    public async Task WhenDisposedFromAnotherFlowDuringSourceWrite_ThenSecondCheckIsSkippedAndSnapshotIsApplied()
    {
        // Arrange
        var handler = new RecordingHandler();
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann" };
        var flowWithoutTransaction = ExecutionContext.Capture()!;

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        writer.DuringWrite = () => ExecutionContext.Run(flowWithoutTransaction, _ => transaction.Dispose(), null);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Equal("Bob", person.FirstName);
    }

    [Fact]
    public async Task WhenDisposedFromAnotherFlowDuringSecondCheck_ThenItsRejectionIsKept()
    {
        // Arrange
        var calls = 0;
        var handler = new RecordingHandler();
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann" };
        var flowWithoutTransaction = ExecutionContext.Capture()!;
        handler.OnCommittingAction = (committingTransaction, _) =>
        {
            if (++calls == 2)
            {
                ExecutionContext.Run(flowWithoutTransaction, _ => committingTransaction.Dispose(), null);
                throw new InvalidOperationException("rejected");
            }
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(2, handler.CommittingCalls);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(writer.Reverted).Property.Name);
    }

    [Fact]
    public async Task WhenSecondCheckRejectsChangeWithRollback_ThenChangeWithoutSourceIsReportedAsNotApplied()
    {
        // Arrange
        var calls = 0;
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, changes) =>
            {
                if (++calls == 2)
                {
                    var firstName = changes.Single(change => change.Property.Name == nameof(Person.FirstName));
                    throw new SubjectTransactionException(
                        "invalid", [], [firstName], [new InvalidOperationException("invalid first name")]);
                }
            }
        };
        var writer = new CountingTransactionWriter { UnwrittenPropertyNames = { nameof(Person.LastName) } };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "Bob";
        person.LastName = "Kim";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Lee", person.LastName);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName)],
            exception.FailedChanges.Select(change => change.Property.Name));
        Assert.Equal("invalid first name", Assert.Single(exception.Errors).Message);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(writer.Reverted).Property.Name);
    }

    [Fact]
    public async Task WhenSecondCheckThrowsOperationCanceled_ThenCommitFailsTerminally()
    {
        // Arrange
        var calls = 0;
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, _) =>
            {
                if (++calls == 2)
                {
                    throw new OperationCanceledException();
                }
            }
        };
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transaction.CommitAsync(CancellationToken.None));

        // Assert
        Assert.IsType<OperationCanceledException>(Assert.Single(exception.Errors));
        Assert.Equal("Ann", person.FirstName);
        Assert.Contains("already been committed", retryException.Message);
    }

    [Fact]
    public async Task WhenRejectionErrorsAlignWithFailedChanges_ThenOnlyErrorsOfCheckedChangesAreKept()
    {
        // Arrange
        var calls = 0;
        var person = default(Person)!;
        var checkedError = new InvalidOperationException("invalid first name");
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, changes) =>
            {
                if (++calls == 2)
                {
                    var firstName = changes.Single(change => change.Property.Name == nameof(Person.FirstName));
                    var uncheckedChange = SubjectPropertyChange.Create(
                        new PropertyReference(person, nameof(Person.LastName)),
                        ChangeOrigin.Local,
                        DateTimeOffset.UtcNow,
                        null,
                        "Lee",
                        "Kim");
                    throw new SubjectTransactionException(
                        "invalid", [], [firstName, uncheckedChange], [checkedError, new InvalidOperationException("invalid last name")]);
                }
            }
        };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(new CountingTransactionWriter());
        person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(nameof(Person.FirstName), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Same(checkedError, Assert.Single(exception.Errors));
    }

    [Fact]
    public async Task WhenWriterMarksAcceptedSlotsAsConfirmed_ThenBothChecksSeeTheCapturedOrigin()
    {
        // Arrange
        var source = new object();
        var originsSeen = new List<ChangeOriginKind[]>();
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, changes) => originsSeen.Add(changes.Select(change => change.Origin.Kind).ToArray())
        };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(new CountingTransactionWriter { ConfirmingSource = source });
        var notifications = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(notifications.Add);
        var person = new Person(context) { FirstName = "Ann" };
        notifications.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, originsSeen.Count);
        Assert.All(originsSeen, origins => Assert.Equal([ChangeOriginKind.Local], origins));
        var applied = Assert.Single(notifications, change => change.Property.Name == nameof(Person.FirstName));
        Assert.Equal(ChangeOriginKind.Confirmed, applied.Origin.Kind);
        Assert.Same(source, applied.Origin.Source);
    }

    [Fact]
    public async Task WhenSecondCheckRejectsChangeOfMarkingWriter_ThenWriterMarkedSlotIsReported()
    {
        // Arrange
        var source = new object();
        var calls = 0;
        var handler = new RecordingHandler
        {
            OnCommittingAction = (_, _) =>
            {
                if (++calls == 2)
                {
                    throw new InvalidOperationException("rejected");
                }
            }
        };
        var writer = new CountingTransactionWriter { ConfirmingSource = source };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("Ann", person.FirstName);
        var failed = Assert.Single(exception.FailedChanges);
        Assert.Equal(ChangeOriginKind.Confirmed, failed.Origin.Kind);
        Assert.Same(source, failed.Origin.Source);
        Assert.Equal(ChangeOriginKind.Confirmed, Assert.Single(writer.Reverted).Origin.Kind);
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

    private sealed class CountingTransactionWriter : ITransactionWriter
    {
        public int WriteCalls { get; private set; }

        public Action? DuringWrite { get; set; }

        public HashSet<string> FailedPropertyNames { get; } = [];

        public HashSet<string> UnwrittenPropertyNames { get; } = [];

        public object? ConfirmingSource { get; set; }

        public List<SubjectPropertyChange> Reverted { get; } = [];

        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            DuringWrite?.Invoke();
            if (ConfirmingSource is not null)
            {
                // Marks accepted slots in place, as the writer contract requires.
                var span = changes.Span;
                for (var index = 0; index < span.Length; index++)
                {
                    if (IsWritten(span[index]))
                    {
                        span[index] = span[index].WithOrigin(ChangeOrigin.Confirmed(ConfirmingSource));
                    }
                }
            }

            var all = changes.ToArray();
            var failed = all.Where(change => FailedPropertyNames.Contains(change.Property.Name)).ToArray();
            var written = all.Where(IsWritten).ToArray();
            Exception[] errors = failed.Length > 0 ? [new InvalidOperationException("source write failed")] : [];
            return new(new SourceWriteResult(written, failed, errors, RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
        {
            Reverted.AddRange(written);
            return new(new SourceRevertResult([], []));
        }

        private bool IsWritten(SubjectPropertyChange change)
        {
            return !FailedPropertyNames.Contains(change.Property.Name) &&
                !UnwrittenPropertyNames.Contains(change.Property.Name);
        }
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
