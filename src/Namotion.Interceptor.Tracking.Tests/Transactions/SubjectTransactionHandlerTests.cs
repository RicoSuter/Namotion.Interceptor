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

    private sealed class RecordingHandler : ITransactionHandler
    {
        public List<(PropertyReference Property, object? Value, ChangeOrigin Origin)> Captured { get; } = [];

        public Func<PropertyReference, object?, Exception?>? RejectCapture { get; set; }

        public List<(RecordingHandler Handler, object? Value)>? CaptureLog { get; init; }

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
        }
    }
}
