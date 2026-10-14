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
        Assert.DoesNotContain(second.Captured, captured => Equals(captured.Value, "Bad"));
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
        handler.OnCommittingAction = (_, _) =>
        {
            firstNameSeenByHandler = person.FirstName;
            notificationsSeenByHandler = notifications.Count;
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.CommittingCalls);
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
        reject = false;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("rejected", exception.Message);
        Assert.Equal(0, notificationsAfterFailure);
        Assert.Equal(1, pendingAfterFailure);
        Assert.Equal("Bob", person.FirstName);
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
        Assert.Equal("Bob", person.FirstName);
        Assert.Null(person.LastName);
    }

    [Fact]
    public async Task WhenDisposedDuringOnCommitting_ThenCommitThrowsAndLockIsReleased()
    {
        // Arrange
        var handler = new RecordingHandler { OnCommittingAction = (transaction, _) => transaction.Dispose() };
        var context = CreateContext();
        context.AddService<ITransactionHandler>(handler);
        var person = new Person(context) { FirstName = "Ann" };
        var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        person.FirstName = "Bob";

        // Act
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        transaction.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var next = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort, cancellationToken: timeout.Token);

        // Assert
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
}
