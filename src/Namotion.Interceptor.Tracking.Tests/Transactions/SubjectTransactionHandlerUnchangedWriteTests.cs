using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Covers writes of an unchanged value, which the equality check drops: in Deferred mode the commit check still
/// receives them, as entries the commit neither writes, applies nor publishes.
/// </summary>
public class SubjectTransactionHandlerUnchangedWriteTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task WhenDeferredAndUnchangedValueIsWritten_ThenOnlyCommitCheckReceivesItAfterPendingChanges(
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
            TransactionFailureHandling.BestEffort, validationMode: TransactionValidationMode.Deferred);
        person.FirstName = "Ann";
        person.LastName = "Kim";
        var pendingChanges = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal([nameof(Person.LastName)], pendingChanges.Select(change => change.Property.Name));
        Assert.Equal([nameof(Person.FirstName), nameof(Person.LastName)], handler.Captured.Select(capture => capture.Property.Name));
        Assert.Equal(expectedChecks, changesSeen.Count);
        Assert.Equal(
            [nameof(Person.LastName), nameof(Person.FirstName)],
            changesSeen[0].Select(change => change.Property.Name));
        Assert.Equal("Ann", changesSeen[0][1].GetOldValue<string>());
        Assert.Equal("Ann", changesSeen[0][1].GetNewValue<string>());
        Assert.All(changesSeen.Skip(1), changes => Assert.Equal(nameof(Person.LastName), Assert.Single(changes).Property.Name));
        Assert.Equal(Enumerable.Repeat<string?>("Ann", expectedChecks), firstNamesSeen);
        Assert.Equal(withWriter ? [nameof(Person.LastName)] : [], writer.ReceivedPropertyNames);
        Assert.Equal("Kim", person.LastName);
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
            TransactionFailureHandling.BestEffort, validationMode: TransactionValidationMode.Deferred);
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
            TransactionFailureHandling.BestEffort, validationMode: TransactionValidationMode.Deferred);
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
            TransactionFailureHandling.BestEffort, validationMode: TransactionValidationMode.Deferred);
        first.FirstName = "Ann";
        second.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(changesSeen);
        Assert.Equal([second, first], changesSeen.Select(change => (object)change.Property.Subject));
        Assert.Equal(["Bob", "Ann"], changesSeen.Select(change => change.GetNewValue<string>()));
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
            TransactionFailureHandling.BestEffort, validationMode: TransactionValidationMode.Deferred);
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
            TransactionFailureHandling.BestEffort, validationMode: TransactionValidationMode.Deferred);
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
            TransactionFailureHandling.BestEffort, validationMode: TransactionValidationMode.Deferred);
        person.FirstName = "Ann";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Empty(handler.Captured);
        Assert.Equal(0, handler.CommittingCalls);
    }
}
