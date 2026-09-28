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

public class SubjectTransactionWriterTests
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

    [Theory]
    [InlineData(TransactionLocking.Exclusive, TransactionFailureHandling.Rollback)]
    [InlineData(TransactionLocking.Exclusive, TransactionFailureHandling.BestEffort)]
    [InlineData(TransactionLocking.Optimistic, TransactionFailureHandling.Rollback)]
    [InlineData(TransactionLocking.Optimistic, TransactionFailureHandling.BestEffort)]
    public async Task WhenWriterResolutionFails_ThenCommitCanBeRetriedAfterCorrectingRegistration(
        TransactionLocking locking, TransactionFailureHandling failureHandling)
    {
        // Arrange
        var context = CreateTransactionContext();
        var writer = new NonMarkingTransactionWriter();
        var conflictingWriter = new NonMarkingTransactionWriter();
        var fallback = InterceptorSubjectContext.Create();
        context.AddService<ITransactionWriter>(writer);
        fallback.AddService<ITransactionWriter>(conflictingWriter);
        context.AddFallbackContext(fallback);
        var person = new Person(context) { FirstName = "original" };
        using var transaction = await context.BeginTransactionAsync(failureHandling, locking);
        person.FirstName = "pending";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Equal(0, writer.WriteCalls);
        Assert.Equal(0, conflictingWriter.WriteCalls);
        var pending = Assert.Single(transaction.GetPendingChanges());
        Assert.Equal("original", pending.GetOldValue<string>());
        Assert.Equal("pending", pending.GetNewValue<string>());

        context.RemoveFallbackContext(fallback);
        person.FirstName = "updated";
        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal("updated", person.FirstName);
        Assert.Equal(1, writer.WriteCalls);
        Assert.Equal(0, conflictingWriter.WriteCalls);
        Assert.Empty(transaction.GetPendingChanges());
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

    [Fact]
    public async Task WhenBestEffortPartiallyApplies_ThenDerivedAndDerivedOfDerivedTrackTheAppliedModel()
    {
        // Arrange
        var context = CreateTransactionContext();
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
        var context = CreateTransactionContext();
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
        var context = CreateTransactionContext();
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
            await RunWithoutAsyncLocalFlowAsync(() => subject.Plain = "external");
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
        var context = CreateTransactionContext();
        context.AddService<ITransactionWriter>(new FailingTransactionWriter());
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

    private sealed class FailingTransactionWriter : ITransactionWriter
    {
        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            return new ValueTask<SourceWriteResult>(new SourceWriteResult(
                Written: [],
                Failed: changes.ToArray(),
                Errors: [new InvalidOperationException("Writer rejected the changes.")],
                RevertState: null));
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
            => new(new SourceRevertResult([], []));
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
        public int WriteCalls { get; private set; }

        public ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            return new(new SourceWriteResult([], [], [], RevertState: null));
        }

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
