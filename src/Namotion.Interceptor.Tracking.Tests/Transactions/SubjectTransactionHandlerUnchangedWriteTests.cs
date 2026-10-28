using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Covers writes of an unchanged value, which the equality check drops: in Deferred mode with FailOnConflict the
/// commit check and the pre-apply check still receive them, as entries the commit neither writes, applies nor publishes.
/// </summary>
public class SubjectTransactionHandlerUnchangedWriteTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task WhenDeferredAndUnchangedValueIsWritten_ThenEveryCheckReceivesItAfterPendingChanges(
        bool withWriter, int expectedChecks)
    {
        // Arrange
        var changesSeen = new List<SubjectPropertyChange[]>();
        var firstNamesSeen = new List<string?>();
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, withWriter ? writer : null);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        handler.OnCommittingAction = (_, changes) =>
        {
            changesSeen.Add(changes);
            firstNamesSeen.Add(person.FirstName);
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Ann";
        person.LastName = "Kim";
        var pendingChanges = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal([nameof(Person.LastName)], pendingChanges.Select(change => change.Property.Name));
        Assert.Equal([nameof(Person.FirstName), nameof(Person.LastName)], handler.Captured.Select(capture => capture.Property.Name));
        Assert.Equal(expectedChecks, changesSeen.Count);
        Assert.All(changesSeen, changes =>
        {
            Assert.Equal([nameof(Person.LastName), nameof(Person.FirstName)], changes.Select(change => change.Property.Name));
            Assert.Equal("Ann", changes[1].GetOldValue<string>());
            Assert.Equal("Ann", changes[1].GetNewValue<string>());
        });
        Assert.Equal(Enumerable.Repeat<string?>("Ann", expectedChecks), firstNamesSeen);
        Assert.Equal(withWriter ? [nameof(Person.LastName)] : [], writer.ReceivedPropertyNames);
        Assert.Equal("Kim", person.LastName);
    }

    [Fact]
    public async Task WhenUnchangedValueChangesDuringSourceWrite_ThenPreApplyCheckReadsLandedValue()
    {
        // Arrange
        var firstNamesSeen = new List<string?>();
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, writer);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        handler.OnCommittingAction = (_, _) => firstNamesSeen.Add(person.FirstName);
        writer.DuringWrite = () => RunOutsideTransactionFlow(() => person.FirstName = "Eve");

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Ann";
        person.LastName = "Kim";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Ann", "Eve"], firstNamesSeen);
    }

    [Fact]
    public async Task WhenLandedValueReadOfUnchangedValueThrowsInPreApplyCheck_ThenSetIsRejectedAndTransactionIsTerminal()
    {
        // Arrange
        var readException = new InvalidOperationException("landed value read failed");
        var readInterceptor = new ThrowingReadInterceptor(nameof(Person.FirstName), readException);
        var writer = new CountingTransactionWriter { DuringWrite = () => readInterceptor.IsArmed = true };
        var context = CreateContext(out var handler, writer);
        context.AddService(readInterceptor);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Ann";
        person.LastName = "Kim";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transaction.CommitAsync(CancellationToken.None));

        // Assert
        Assert.Same(readException, Assert.Single(exception.Errors));
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(nameof(Person.LastName), Assert.Single(writer.Reverted).Property.Name);
        Assert.Equal(1, writer.WriteCalls);
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Contains("already been committed", retryException.Message);
        Assert.Equal("Lee", person.LastName);
    }

    [Fact]
    public async Task WhenDeferredAndSourceWriteFails_ThenPreApplyCheckReceivesRemainingChangesFollowedByUnchangedValue()
    {
        // Arrange
        var changesSeen = new List<SubjectPropertyChange[]>();
        var writer = new CountingTransactionWriter { FailedPropertyNames = { nameof(Person.LastName) } };
        var context = CreateContext(out var handler, writer);
        var first = new Person(context) { FirstName = "Ann" };
        var second = new Person(context) { FirstName = "Eve", LastName = "Lee" };
        handler.OnCommittingAction = (_, changes) => changesSeen.Add(changes);

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        first.FirstName = "Bob";
        second.LastName = "Kim";
        second.FirstName = "Eve";
        await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(2, changesSeen.Count);
        var preApplyChanges = changesSeen[1];
        Assert.Equal(
            [(first, nameof(Person.FirstName)), (second, nameof(Person.FirstName))],
            preApplyChanges.Select(change => ((Person)change.Property.Subject, change.Property.Name)));
        Assert.Equal("Bob", preApplyChanges[0].GetNewValue<string>());
        Assert.Equal("Eve", preApplyChanges[1].GetOldValue<string>());
        Assert.Equal("Eve", preApplyChanges[1].GetNewValue<string>());
        Assert.Equal("Bob", first.FirstName);
        Assert.Equal("Lee", second.LastName);
    }

    [Fact]
    public async Task WhenDeferredAndEverySourceWriteFails_ThenUnchangedValueIsNotCheckedAgain()
    {
        // Arrange
        var writer = new CountingTransactionWriter { FailedPropertyNames = { nameof(Person.LastName) } };
        var context = CreateContext(out var handler, writer);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Ann";
        person.LastName = "Kim";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(nameof(Person.LastName), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Equal("Lee", person.LastName);
    }

    [Fact]
    public async Task WhenDeferredAndPendingChangeIsWrittenBack_ThenCommitCheckReceivesLandedValue()
    {
        // Arrange
        var changesSeen = new List<SubjectPropertyChange[]>();
        var context = CreateContext(out var handler);
        var person = new Person(context) { FirstName = "Ann" };
        handler.OnCommittingAction = (_, changes) => changesSeen.Add(changes);

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Bob";
        person.FirstName = "Ann";
        var pendingChanges = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Empty(pendingChanges);
        var change = Assert.Single(Assert.Single(changesSeen));
        Assert.Equal(nameof(Person.FirstName), change.Property.Name);
        Assert.Equal("Ann", change.GetOldValue<string>());
        Assert.Equal("Ann", change.GetNewValue<string>());
        Assert.Equal("Ann", person.FirstName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDeferredAndOnlyUnchangedValuesAreWritten_ThenCommitCheckRunsAndNothingIsWritten(bool withWriter)
    {
        // Arrange
        var publishedCount = 0;
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, withWriter ? writer : null);
        var person = new Person(context) { FirstName = "Ann" };
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(_ => publishedCount++);

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Ann";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.CommittingCalls);
        Assert.Equal(0, writer.WriteCalls);
        Assert.Equal(0, publishedCount);
    }

    [Fact]
    public async Task WhenUnchangedValueIsWrittenBeforeAnyHandlerIsIncluded_ThenCommitCheckStillReceivesIt()
    {
        // Arrange
        var handler = new RecordingHandler();
        var transactionContext = CreateContext();
        var subjectContext = InterceptorSubjectContext.Create();
        subjectContext.AddService<ITransactionHandler>(handler);
        subjectContext.AddFallbackContext(transactionContext);
        var first = new Person(transactionContext) { FirstName = "Ann" };
        var second = new Person(subjectContext);
        SubjectPropertyChange[]? changesSeen = null;
        handler.OnCommittingAction = (_, changes) => changesSeen = changes;

        // Act
        using var transaction = await transactionContext.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        first.FirstName = "Ann";
        second.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(changesSeen);
        Assert.Equal([second, first], changesSeen.Select(change => (object)change.Property.Subject));
        Assert.Equal(["Bob", "Ann"], changesSeen.Select(change => change.GetNewValue<string>()));
    }

    [Fact]
    public async Task WhenWrittenBackPropertyChangesBeforeCommit_ThenCommitConflictsUntilTheNewValueIsWritten()
    {
        // Arrange
        var context = CreateContext(out _);
        var person = new Person(context) { FirstName = "Ann" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict,
            checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Bob";
        person.FirstName = "Ann";
        await RunOnThreadPoolWithoutFlowAsync(() => person.FirstName = "Carl");

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        person.FirstName = "Carl";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(nameof(Person.FirstName), Assert.Single(exception.ConflictingProperties).Name);
        Assert.Equal("Carl", person.FirstName);
    }

    [Fact]
    public async Task WhenDeferredWithoutHandlersAndUnchangedValueChangesBeforeCommit_ThenCommitSucceeds()
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "Ann" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict,
            checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Ann";
        await RunOnThreadPoolWithoutFlowAsync(() => person.FirstName = "Carl");

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Carl", person.FirstName);
    }

    [Fact]
    public async Task WhenDeferredWithIgnore_ThenUnchangedAndWrittenBackValuesAreNotRecorded()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.Ignore,
            checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Ann";
        person.LastName = "Kim";
        person.LastName = "Lee";
        await RunOnThreadPoolWithoutFlowAsync(() => person.FirstName = "Carl");

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Kim", "Lee"], handler.Captured.Select(capture => capture.Value));
        Assert.Equal(0, handler.CommittingCalls);
        Assert.Equal("Carl", person.FirstName);
        Assert.Equal("Lee", person.LastName);
    }

    [Fact]
    public async Task WhenImmediateAndUnchangedValueIsWritten_ThenHandlersAreNotCalled()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var person = new Person(context) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Ann";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Empty(handler.Captured);
        Assert.Equal(0, handler.CommittingCalls);
    }

    [Fact]
    public async Task WhenDeferredAndUnchangedValueIsRejectedAtCapture_ThenSetterThrowsAndNothingIsRecorded()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var person = new Person(context) { FirstName = "Ann" };
        handler.RejectCapture = (_, _) => new InvalidOperationException("rejected");

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        var exception = Assert.Throws<InvalidOperationException>(() => person.FirstName = "Ann");
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("rejected", exception.Message);
        Assert.Equal(0, handler.CommittingCalls);
    }

    [Fact]
    public async Task WhenDeferredAndUnchangedSettableDerivedValueIsWritten_ThenItIsDropped()
    {
        // Arrange
        var publishedCount = 0;
        var context = CreateContext(out var handler);
        var person = new DerivedSetterPerson(context) { Nickname = "Al" };
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(_ => publishedCount++);

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        person.Nickname = "Al";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Empty(handler.Captured);
        Assert.Equal(0, handler.CommittingCalls);
        Assert.Equal(0, publishedCount);
    }

    [Fact]
    public async Task WhenDeferredAndUnchangedValueOfSubjectOutsideTransactionContextIsWritten_ThenItIsDropped()
    {
        // Arrange
        var context = CreateContext(out var handler);
        var otherContext = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(otherContext) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        person.FirstName = "Ann";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Empty(handler.Captured);
        Assert.Equal(0, handler.CommittingCalls);
    }

    private sealed class ThrowingReadInterceptor(string propertyName, Exception exception) : IReadInterceptor
    {
        public bool IsArmed { get; set; }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            if (IsArmed && context.Property.Name == propertyName)
            {
                throw exception;
            }

            return next(ref context);
        }
    }
}
