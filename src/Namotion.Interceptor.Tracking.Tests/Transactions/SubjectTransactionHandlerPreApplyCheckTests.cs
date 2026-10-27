using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

/// <summary>
/// Covers the pre-apply check: the handler check that runs again after the source writes, right before the local apply.
/// </summary>
public class SubjectTransactionHandlerPreApplyCheckTests
{
    [Fact]
    public async Task WhenPropertyChangesOutsideTransactionDuringSourceWrite_ThenPreApplyCheckSeesLandedValue()
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, writer);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        var lastNamesSeen = new List<string?>();
        handler.OnCommittingAction = (_, _) => lastNamesSeen.Add(person.LastName);
        writer.DuringWrite = () => RunOutsideTransactionFlow(() => person.LastName = "Kim");

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
    public async Task WhenHandlerWritesDuringPreApplyCheck_ThenAllChangesFail()
    {
        // Arrange
        var calls = 0;
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, writer);
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
    public async Task WhenPreApplyCheckRejectsChange_ThenNoApplyRunsAndTransactionIsTerminal(TransactionFailureHandling failureHandling)
    {
        // Arrange
        var writer = new CountingTransactionWriter { UnwrittenPropertyNames = { nameof(Person.FirstName) } };
        var context = CreateContext(out var handler, writer);
        handler.RejectPreApplyCheck(nameof(Person.LastName), "invalid last name");
        var probe = new CommitWriteProbe(handler);
        context.AddService(probe);
        var person = new Person(context) { FirstName = "Ann", LastName = "Lee" };
        probe.Writes.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(failureHandling);
        person.FirstName = "Bob";
        person.LastName = "Kim";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Lee", person.LastName);
        Assert.Empty(probe.Writes);
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(
            [nameof(Person.FirstName), nameof(Person.LastName)],
            exception.FailedChanges.Select(change => change.Property.Name));
        Assert.Equal("invalid last name", Assert.Single(exception.Errors).Message);
        Assert.Equal(nameof(Person.LastName), Assert.Single(writer.Reverted).Property.Name);

        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transaction.CommitAsync(CancellationToken.None));
        Assert.Contains("already been committed", retryException.Message);
    }

    [Fact]
    public async Task WhenSourceWriteFails_ThenPreApplyCheckSeesOnlyLandingChangesAndLandedValueOfFailedProperty()
    {
        // Arrange
        var person = default(Person)!;
        var calls = new List<(string[] Changes, string? FirstName, string? LastName)>();
        var writer = new CountingTransactionWriter { FailedPropertyNames = { nameof(Person.LastName) } };
        var context = CreateContext(out var handler, writer);
        handler.OnCommittingAction = (_, changes) =>
        {
            calls.Add((changes.Select(change => change.Property.Name).ToArray(), person.FirstName, person.LastName));
            if (person.FirstName == person.LastName)
            {
                throw new InvalidOperationException("first and last name must differ");
            }
        };
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
    public async Task WhenEverySourceWriteFails_ThenOnCommittingIsNotCalledAgainAndNothingIsApplied()
    {
        // Arrange
        var writer = new CountingTransactionWriter { FailedPropertyNames = { nameof(Person.FirstName) } };
        var context = CreateContext(out var handler, writer);
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
    public async Task WhenDisposedFromAnotherFlowDuringSourceWrite_ThenSetIsRejectedAndSourceIsReverted()
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, writer);
        var person = new Person(context) { FirstName = "Ann" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        writer.DuringWrite = () => RunOutsideTransactionFlow(transaction.Dispose);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(1, handler.CommittingCalls);
        Assert.IsType<ObjectDisposedException>(Assert.Single(exception.Errors));
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(writer.Reverted).Property.Name);
    }

    [Fact]
    public async Task WhenDisposedFromAnotherFlowDuringPreApplyCheck_ThenItsRejectionIsKept()
    {
        // Arrange
        var calls = 0;
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, writer);
        var person = new Person(context) { FirstName = "Ann" };
        handler.OnCommittingAction = (committingTransaction, _) =>
        {
            if (++calls == 2)
            {
                RunOutsideTransactionFlow(committingTransaction.Dispose);
                throw new InvalidOperationException("rejected");
            }
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(2, handler.CommittingCalls);
        Assert.Equal("rejected", Assert.IsType<InvalidOperationException>(Assert.Single(exception.Errors)).Message);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(writer.Reverted).Property.Name);
    }

    [Fact]
    public async Task WhenPreApplyCheckThrowsOperationCanceled_ThenCommitFailsTerminally()
    {
        // Arrange
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, writer);
        handler.RejectPreApplyCheck(new OperationCanceledException());
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenPreApplyRejectionReportsErrorsOfUncheckedChanges_ThenEveryErrorIsReported(bool withFailedChanges)
    {
        // Arrange
        var calls = 0;
        var person = default(Person)!;
        var firstNameError = new InvalidOperationException("invalid first name");
        var lastNameError = new InvalidOperationException("invalid last name");
        var writer = new CountingTransactionWriter();
        var context = CreateContext(out var handler, writer);
        handler.OnCommittingAction = (_, changes) =>
        {
            if (++calls == 2)
            {
                // LastName is not part of the transaction, so its change and error are unchecked.
                var uncheckedChange = SubjectPropertyChange.Create(
                    new PropertyReference(person, nameof(Person.LastName)),
                    ChangeOrigin.Local,
                    DateTimeOffset.UtcNow,
                    null,
                    "Lee",
                    "Kim");
                IReadOnlyList<SubjectPropertyChange> failedChanges = withFailedChanges
                    ? [changes.Single(change => change.Property.Name == nameof(Person.FirstName)), uncheckedChange]
                    : [];
                throw new SubjectTransactionException("invalid", [], failedChanges, [firstNameError, lastNameError]);
            }
        };
        person = new Person(context) { FirstName = "Ann", LastName = "Lee" };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal([firstNameError, lastNameError], exception.Errors);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Lee", person.LastName);
        Assert.Equal(nameof(Person.FirstName), Assert.Single(writer.Reverted).Property.Name);
    }

    [Fact]
    public async Task WhenWriterMarksAcceptedSlotsAsConfirmed_ThenBothChecksSeeTheCapturedOrigin()
    {
        // Arrange
        var source = new object();
        var originsSeen = new List<ChangeOriginKind[]>();
        var context = CreateContext(out var handler, new CountingTransactionWriter { ConfirmingSource = source });
        handler.OnCommittingAction = (_, changes) => originsSeen.Add(changes.Select(change => change.Origin.Kind).ToArray());
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
    public async Task WhenPreApplyCheckRejectsChangeOfMarkingWriter_ThenWriterMarkedSlotIsReported()
    {
        // Arrange
        var source = new object();
        var writer = new CountingTransactionWriter { ConfirmingSource = source };
        var context = CreateContext(out var handler, writer);
        handler.RejectPreApplyCheck(new InvalidOperationException("rejected"));
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

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task WhenChangeIsCanceledByWriteBack_ThenNoCheckReceivesItAndEachReadsLandedValue(bool withWriter, int expectedChecks)
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
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        person.FirstName = "Ann";
        person.LastName = "Kim";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedChecks, changesSeen.Count);
        Assert.All(changesSeen, changes =>
        {
            var change = Assert.Single(changes);
            Assert.Equal(nameof(Person.LastName), change.Property.Name);
            Assert.Equal("Kim", change.GetNewValue<string>());
        });
        Assert.Equal(Enumerable.Repeat<string?>("Ann", expectedChecks), firstNamesSeen);
        Assert.Equal(withWriter ? 1 : 0, writer.WriteCalls);
        Assert.Equal("Ann", person.FirstName);
        Assert.Equal("Kim", person.LastName);
    }
}
