using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using System.Collections;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Tracking.Tests.Models;
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionWriterTests
{
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

    [Fact]
    public async Task WhenBestEffortPartiallyApplies_ThenDerivedAndDerivedOfDerivedTrackTheAppliedModel()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "original",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "applied";
            subject.Failing = "fails";
            subject.ThrowOnFailingWrite = true;
            await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Collection(
            changes,
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.Combined), "original|d0", "applied|d0"),
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.CombinedAgain), "[original|d0]", "[applied|d0]"));
        Assert.Equal("applied", subject.Plain);
        Assert.Equal("applied|d0", subject.Combined);
        Assert.Equal("[applied|d0]", subject.CombinedAgain);

        changes.Clear();
        subject.DerivedWithSetter = "d1";
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "applied|d0" &&
            change.GetNewValue<string>() == "applied|d1");
    }

    [Fact]
    public async Task WhenRollbackRevertsALocalApply_ThenDerivedAndDerivedOfDerivedReturnToTheOriginalModel()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "original",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            subject.Plain = "temporarily-applied";
            subject.Failing = "fails";
            subject.ThrowOnFailingWrite = true;
            await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Collection(
            changes,
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.Combined), "original|d0", "temporarily-applied|d0"),
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.CombinedAgain), "[original|d0]", "[temporarily-applied|d0]"),
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.Combined), "temporarily-applied|d0", "original|d0"),
            change => AssertDerivedChange(change, nameof(TransactionCascadeSubject.CombinedAgain), "[temporarily-applied|d0]", "[original|d0]"));
        Assert.Equal("original", subject.Plain);
        Assert.Equal("original|d0", subject.Combined);
        Assert.Equal("[original|d0]", subject.CombinedAgain);

        changes.Clear();
        subject.DerivedWithSetter = "d1";
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "original|d0" &&
            change.GetNewValue<string>() == "original|d1");
    }

    [Fact]
    public async Task WhenConflictPreventsApply_ThenDerivedTrackingRemainsOnTheExternalModel()
    {
        // Arrange
        var context = CreateContext();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "original",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(
                   TransactionFailureHandling.BestEffort,
                   TransactionLocking.Optimistic,
                   conflictBehavior: TransactionConflictBehavior.FailOnConflict))
        {
            subject.Plain = "transaction";
            await RunOnThreadPoolWithoutFlowAsync(() => subject.Plain = "external");
            changes.Clear();

            await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            Assert.Empty(changes);
        }

        // Assert
        Assert.Equal("external", subject.Plain);
        Assert.Equal("external|d0", subject.Combined);
        Assert.Equal("[external|d0]", subject.CombinedAgain);

        subject.DerivedWithSetter = "d1";
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "external|d0" &&
            change.GetNewValue<string>() == "external|d1");
    }

    [Fact]
    public async Task WhenWriterFailurePreventsApply_ThenNoDerivedTrackingStateChanges()
    {
        // Arrange
        var context = CreateContext();
        context.AddService<ITransactionWriter>(
            new CountingTransactionWriter { FailedPropertyNames = { nameof(TransactionCascadeSubject.Plain) } });
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "original",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            subject.Plain = "not-applied";
            await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Empty(changes);
        Assert.Equal("original", subject.Plain);
        Assert.Equal("original|d0", subject.Combined);
        Assert.Equal("[original|d0]", subject.CombinedAgain);

        subject.DerivedWithSetter = "d1";
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "original|d0" &&
            change.GetNewValue<string>() == "original|d1");
    }

    [Theory]
    [InlineData(false, TransactionFailureHandling.BestEffort)]
    [InlineData(false, TransactionFailureHandling.Rollback)]
    [InlineData(true, TransactionFailureHandling.BestEffort)]
    [InlineData(true, TransactionFailureHandling.Rollback)]
    public async Task WhenCommitTimesOutWhileRevertingCheckedSet_ThenCommitFailsTerminallyAndModelIsUntouched(
        bool preApplyCheckRejects, TransactionFailureHandling failureHandling)
    {
        // Arrange
        var context = CreateContext(out var handler, new CountingTransactionWriter { RevertsUntilCanceled = true });
        if (preApplyCheckRejects)
        {
            handler.RejectPreApplyCheck(new InvalidOperationException("rejected"));
        }
        else
        {
            context.AddService(new CommitWriteProbe(handler) { ThrowOnConsumedProperty = nameof(Person.LastName) });
        }

        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        using var transaction = await context.BeginTransactionAsync(
            failureHandling, commitTimeout: TimeSpan.FromMilliseconds(50));
        person.FirstName = "Bob";
        person.LastName = "Kim";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        string? landedFirstName = null;
        string? landedLastName = null;
        RunOutsideTransactionFlow(() =>
        {
            landedFirstName = person.FirstName;
            landedLastName = person.LastName;
        });
        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transaction.CommitAsync(CancellationToken.None));

        // Assert
        // Every change fails, and each source revert that timed out lists its change again as stuck.
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName), nameof(Person.FirstName), nameof(Person.LastName)],
            exception.FailedChanges.Select(change => change.Property.Name));
        Assert.Equal(2, exception.Errors.Count);
        Assert.Equal(preApplyCheckRejects ? "rejected" : "apply failed", exception.Errors[0].Message);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.Errors[1]);
        Assert.Equal("Ann", landedFirstName);
        Assert.Equal("Lee", landedLastName);
        Assert.Equal(2, handler.CommittingCalls);
        Assert.Contains("already been committed", retryException.Message);
    }

    [Fact]
    public async Task WhenCommitTimesOutWhileRevertingAfterSourceFailureInRollback_ThenModelIsUntouchedAndRetryCommits()
    {
        // Arrange
        var context = CreateContext();
        var writer = new CountingTransactionWriter { FailedPropertyNames = { nameof(Person.LastName) }, RevertsUntilCanceled = true };
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, commitTimeout: TimeSpan.FromMilliseconds(50));
        person.FirstName = "Bob";
        person.LastName = "Kim";

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        string? landedFirstName = null;
        RunOutsideTransactionFlow(() => landedFirstName = person.FirstName);
        writer.FailedPropertyNames.Clear();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Ann", landedFirstName);
        Assert.Equal("Bob", person.FirstName);
        Assert.Equal("Kim", person.LastName);
        Assert.Equal(2, writer.WriteCalls);
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName), nameof(Person.FirstName), nameof(Person.LastName)],
            writer.ReceivedPropertyNames);
    }

    [Theory]
    [InlineData(TransactionFailureHandling.Rollback, false)]
    [InlineData(TransactionFailureHandling.Rollback, true)]
    [InlineData(TransactionFailureHandling.BestEffort, false)]
    [InlineData(TransactionFailureHandling.BestEffort, true)]
    public async Task WhenWriterReturnsDefaultResult_ThenCommitAppliesTheChanges(
        TransactionFailureHandling failureHandling, bool withHandler)
    {
        // Arrange
        var writer = new CountingTransactionWriter { ReturnedWriteResult = default(SourceWriteResult) };
        var context = CreateContext();
        context.AddService<ITransactionWriter>(writer);
        if (withHandler)
        {
            context.AddService<ITransactionHandler>(new RecordingHandler());
        }

        var person = new Person(context) { FirstName = "Ann" };
        using var transaction = await context.BeginTransactionAsync(failureHandling);
        person.FirstName = "Bob";

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Bob", person.FirstName);
        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transaction.CommitAsync(CancellationToken.None));
        Assert.Contains("already been committed", retryException.Message);
    }

    [Theory]
    [InlineData(TransactionFailureHandling.Rollback)]
    [InlineData(TransactionFailureHandling.BestEffort)]
    public async Task WhenWriterReturnsDefaultRevertResultAfterApplyFailure_ThenCommitFailsTerminally(
        TransactionFailureHandling failureHandling)
    {
        // Arrange
        var context = CreateContext();
        var writer = new CountingTransactionWriter { ReturnedRevertResult = default(SourceRevertResult) };
        context.AddService<ITransactionWriter>(writer);
        context.AddService(new ValueRejectingWriteInterceptor { Rejected = { (nameof(Person.LastName), "Kim") } });
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        using var transaction = await context.BeginTransactionAsync(failureHandling);
        person.FirstName = "Bob";
        person.LastName = "Kim";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal([nameof(Person.LastName)], exception.FailedChanges.Select(change => change.Property.Name));
        Assert.Contains(writer.Reverted, change => change.Property.Name == nameof(Person.LastName));
        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transaction.CommitAsync(CancellationToken.None));
        Assert.Contains("already been committed", retryException.Message);
        Assert.Equal(1, writer.WriteCalls);
    }

    [Fact]
    public async Task WhenWriterResultThrowsWhenRead_ThenCommitFailsTerminally()
    {
        // Arrange
        var context = CreateContext();
        var writer = new CountingTransactionWriter
        {
            ReturnedWriteResult = new SourceWriteResult([], new UnreadableList(), [], RevertState: null)
        };
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "Bob";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        var error = Assert.Single(exception.Errors);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("unreadable", error.Message);
        Assert.Same(error, exception.InnerException);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal([nameof(Person.FirstName)], exception.FailedChanges.Select(change => change.Property.Name));
        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transaction.CommitAsync(CancellationToken.None));
        Assert.Contains("already been committed", retryException.Message);
        Assert.Equal(1, writer.WriteCalls);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenApplyAndSourceRevertFailForOnePropertyInBestEffortWithoutHandlers_ThenStuckPropertyIsListedTwice()
    {
        // Arrange
        var context = CreateContext();
        context.AddService<ITransactionWriter>(new CountingTransactionWriter { FailedRevertPropertyNames = { nameof(Person.LastName) } });
        context.AddService(new ValueRejectingWriteInterceptor
        {
            Rejected = { (nameof(Person.FirstName), "Bob"), (nameof(Person.LastName), "Kim") }
        });
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        person.LastName = "Kim";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName), nameof(Person.LastName)],
            exception.FailedChanges.Select(change => change.Property.Name));
    }

    [Fact]
    public async Task WhenApplyAndSourceRevertFailForOnePropertyInRollbackWithoutHandlers_ThenItIsListedTwice()
    {
        // Arrange
        var context = CreateContext();
        context.AddService<ITransactionWriter>(
            new CountingTransactionWriter { FailedRevertPropertyNames = { nameof(Person.FirstName), nameof(Person.LastName) } });
        context.AddService(new ValueRejectingWriteInterceptor { Rejected = { (nameof(Person.LastName), "Kim") } });
        var person = new Person(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        person.FirstName = "Bob";
        person.LastName = "Kim";

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(
            [nameof(Person.LastName), nameof(Person.FirstName), nameof(Person.LastName)],
            exception.FailedChanges.Select(change => change.Property.Name));
    }

    [Theory]
    [InlineData(true, TransactionFailureHandling.Rollback, true)]
    [InlineData(true, TransactionFailureHandling.Rollback, false)]
    [InlineData(true, TransactionFailureHandling.BestEffort, false)]
    [InlineData(false, TransactionFailureHandling.Rollback, false)]
    public async Task WhenIgnoreCommitRevertsAfterAConcurrentWrite_ThenRevertRestoresTheValueLandedAtTheCommit(
        bool withWriter, TransactionFailureHandling failureHandling, bool sourceWriteFails)
    {
        // Arrange
        var context = CreateContext();
        var writer = new CountingTransactionWriter();
        if (withWriter)
        {
            context.AddService<ITransactionWriter>(writer);
        }

        if (sourceWriteFails)
        {
            writer.FailedPropertyNames.Add(nameof(Person.LastName));
        }
        else
        {
            // BestEffort without handlers reverts only the source write of the failed apply, so FirstName fails there.
            context.AddService(new ValueRejectingWriteInterceptor
            {
                Rejected = { failureHandling == TransactionFailureHandling.BestEffort ? (nameof(Person.FirstName), "Bob") : (nameof(Person.LastName), "Kim") }
            });
        }

        var person = new Person(context) { FirstName = "Ann" };
        using var transaction = await context.BeginTransactionAsync(
            failureHandling, TransactionLocking.Optimistic, conflictBehavior: TransactionConflictBehavior.Ignore);
        var source = new object();
        var changedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-2);
        var receivedTimestamp = changedTimestamp.AddSeconds(1);
        new PropertyReference(person, nameof(Person.FirstName))
            .SetValueFromSource(source, changedTimestamp, receivedTimestamp, "Bob");
        person.LastName = "Kim";
        RunOutsideTransactionFlow(() => person.FirstName = "External");

        // Act
        await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        transaction.Dispose();
        Assert.Equal("External", person.FirstName);
        if (withWriter)
        {
            var revertedFirstName = Assert.Single(writer.Reverted, change => change.Property.Name == nameof(Person.FirstName));
            Assert.Equal("External", revertedFirstName.GetOldValue<string>());
            Assert.Equal(ChangeOriginKind.FromSource, revertedFirstName.Origin.Kind);
            Assert.Same(source, revertedFirstName.Origin.Source);
            Assert.Equal(changedTimestamp, revertedFirstName.ChangedTimestamp);
            Assert.Equal(receivedTimestamp, revertedFirstName.ReceivedTimestamp);
        }
    }

    private sealed class UnreadableList : IReadOnlyList<SubjectPropertyChange>
    {
        public int Count => throw new InvalidOperationException("unreadable");

        public SubjectPropertyChange this[int index] => throw new InvalidOperationException("unreadable");

        public IEnumerator<SubjectPropertyChange> GetEnumerator() => throw new InvalidOperationException("unreadable");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void AssertDerivedChange(
        SubjectPropertyChange change,
        string propertyName,
        string oldValue,
        string newValue)
    {
        Assert.Equal(propertyName, change.Property.Name);
        Assert.Equal(oldValue, change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Fact]
    public async Task WhenCustomWriterMarksSnapshotInPlace_ThenApplyNotificationsCarryThatSource()
    {
        // Arrange
        var source = new object();
        var changes = new List<SubjectPropertyChange>();
        var context = CreateContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.AddService<ITransactionWriter>(new CountingTransactionWriter { ConfirmingSource = source });

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

    [Fact]
    public async Task WhenCustomWriterDoesNotMarkSnapshot_ThenApplyNotificationsCarryNoSource()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = CreateContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.AddService<ITransactionWriter>(new CountingTransactionWriter());

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

    [Fact]
    public async Task WhenAllPendingChangesAreWrittenBack_ThenCommitCallsNoWriterAndPublishesNothing()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.AddService<ITransactionWriter>(writer);

        var person = new Person(context) { FirstName = "Ann", LastName = "Smith" };
        changes.Clear();

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        person.LastName = "Jones";
        person.FirstName = "Ann";
        person.LastName = "Smith";

        // Act
        var pendingBeforeCommit = transaction.GetPendingChanges();
        var firstNameInsideTransaction = person.FirstName;
        var lastNameInsideTransaction = person.LastName;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Empty(pendingBeforeCommit);
        Assert.Equal("Ann", firstNameInsideTransaction);
        Assert.Equal("Smith", lastNameInsideTransaction);
        Assert.Equal(0, writer.WriteCalls);
        Assert.Empty(changes);
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Smith", person.LastName);
    }

    [Fact]
    public async Task WhenCommitIsRetriedAfterConflict_ThenWrittenBackPropertyStaysUnchanged()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var writer = new CountingTransactionWriter();
        var context = CreateContext();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.AddService<ITransactionWriter>(writer);

        var person = new Person(context) { FirstName = "Ann" };
        changes.Clear();

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);
        person.FirstName = "Bob";
        person.FirstName = "Ann";
        person.LastName = "Smith";

        await RunOnThreadPoolWithoutFlowAsync(() => person.LastName = "External");
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        await RunOnThreadPoolWithoutFlowAsync(() => person.LastName = null);
        changes.Clear();

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal([nameof(Person.LastName)], writer.ReceivedPropertyNames);
        Assert.DoesNotContain(changes, c => c.Property.Name == nameof(Person.FirstName));
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Smith", person.LastName);
    }

    [Fact]
    public async Task WhenWrittenBackWithoutEqualityCheck_ThenCommitWritesLandedValue()
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = InterceptorSubjectContext.Create().WithTransactions();
        context.AddService<ITransactionWriter>(writer);
        var person = new Person(context) { FirstName = "Ann" };

        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        person.FirstName = "Ann";

        // Act
        var pendingBeforeCommit = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Ann", Assert.Single(pendingBeforeCommit).GetNewValue<string>());
        Assert.Equal([nameof(Person.FirstName)], writer.ReceivedPropertyNames);
        Assert.Equal("Ann", person.FirstName);
    }

    [Fact]
    public async Task WhenWrittenBackWithoutEqualityCheckAndChangedConcurrently_ThenCommitConflicts()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithTransactions();
        context.AddService<ITransactionWriter>(new CountingTransactionWriter());
        var person = new Person(context) { FirstName = "Ann" };

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict);
        person.FirstName = "Bob";
        person.FirstName = "Ann";
        RunOutsideTransactionFlow(() => person.FirstName = "External");

        // Act & Assert
        await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
    }
}
