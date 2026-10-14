using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Validation.Tests.Models;

namespace Namotion.Interceptor.Validation.Tests;

public class TransactionValidationTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => new MotorSpeedValidator());
    }

    [Fact]
    public async Task WhenDeferredAndIntermediateValueIsInvalid_ThenFinalValueCommits()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 500;
        motor.MotorSpeed = 50;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(50, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenDeferredAndInvalidValueIsRestoredToLandedValue_ThenCommitSucceeds()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 500;
        motor.MotorSpeed = 0;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenDeferredAndDependencyIsWrittenAfterDependent_ThenCommitSucceeds()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 150;
        motor.MaxAllowedSpeed = 200;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(200, motor.MaxAllowedSpeed);
        Assert.Equal(150, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenFinalSetIsInvalid_ThenCommitThrowsBeforeAnythingIsWritten()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);
        var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 150;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        transaction.Dispose();

        // Assert
        var failed = Assert.Single(exception.FailedChanges);
        Assert.Equal(nameof(Motor.MotorSpeed), failed.Property.Name);
        Assert.IsType<ValidationException>(Assert.Single(exception.Errors));
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(0, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenFinalSetIsInvalid_ThenTransactionCanBeFixedAndRetried()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 150;
        await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        motor.MaxAllowedSpeed = 200;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(150, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenImmediateAndWriteIsInvalid_ThenSetterThrows()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);

        // Act & Assert
        Assert.Throws<ValidationException>(() => motor.MotorSpeed = 150);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenImmediateAndLimitIsLoweredLater_ThenCommitRejectsFinalSet()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        motor.MaxAllowedSpeed = 200;
        motor.MotorSpeed = 150;
        motor.MaxAllowedSpeed = 120;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Equal(nameof(Motor.MotorSpeed), Assert.Single(exception.FailedChanges).Property.Name);
    }

    [Fact]
    public async Task WhenImmediateAndRewriteShiftsDependencyOrder_ThenCommitSucceeds()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        motor.MotorSpeed = 50;
        motor.MaxAllowedSpeed = 200;
        motor.MotorSpeed = 150;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(150, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenHookChangesValueAtApply_ThenAppliedValueIsValidated()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { IncrementSpeedOnWrite = true };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        motor.MotorSpeed = 99;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.IsType<ValidationException>(Assert.Single(exception.Errors));
        Assert.Equal(0, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenChangeHandlerWritesDuringApply_ThenThatWriteIsValidated()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { SpeedToWriteOnLimitChange = 1000 };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, validationMode: TransactionValidationMode.Deferred);
        motor.MaxAllowedSpeed = 200;

        // Act & Assert
        await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Equal(0, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenInterceptorIsRegisteredDirectly_ThenDeferredCommitIsStillValidated()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions()
            .WithService(() => new ValidationInterceptor())
            .WithService<IPropertyValidator>(() => new MotorSpeedValidator());
        var motor = new Motor(context);
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 150;

        // Act & Assert
        await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task WhenLimitIsLoweredWhileSourcesAreWritten_ThenSecondCheckRejectsAndRevertsSources()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MaxAllowedSpeed = 200 };
        var writer = new LimitLoweringTransactionWriter(motor);
        context.AddService<ITransactionWriter>(writer);
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 150;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(nameof(Motor.MotorSpeed), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Equal(0, motor.MotorSpeed);
        Assert.Equal(100, motor.MaxAllowedSpeed);
        Assert.Single(writer.Reverted);
    }

    [Fact]
    public void WhenWriteIsOutsideTransaction_ThenItIsValidated()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);

        // Act & Assert
        Assert.Throws<ValidationException>(() => motor.MotorSpeed = 150);
        Assert.Equal(0, motor.MotorSpeed);
    }

    /// <summary>
    /// Rejects a MotorSpeed above MaxAllowedSpeed. Reads MaxAllowedSpeed through the getter, so inside a transaction
    /// it sees the transaction's value.
    /// </summary>
    private sealed class MotorSpeedValidator : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            if (context.Property.Name == nameof(Motor.MotorSpeed) &&
                context.Value is int speed &&
                context.Property.Subject is Motor motor &&
                speed > motor.MaxAllowedSpeed)
            {
                return [new ValidationResult($"MotorSpeed {speed} exceeds MaxAllowedSpeed {motor.MaxAllowedSpeed}.")];
            }

            return [];
        }
    }

    /// <summary>
    /// Accepts every change and lowers MaxAllowedSpeed to 100 from a flow without the transaction while the source
    /// write is in flight, like an inbound source update.
    /// </summary>
    private sealed class LimitLoweringTransactionWriter(Motor motor) : ITransactionWriter
    {
        public List<SubjectPropertyChange> Reverted { get; } = [];

        public async ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            Task task;
            var flowControl = ExecutionContext.SuppressFlow();
            try
            {
                task = Task.Run(() => motor.MaxAllowedSpeed = 100, cancellationToken);
            }
            finally
            {
                flowControl.Undo();
            }

            await task;
            return new SourceWriteResult(changes.ToArray(), [], [], RevertState: null);
        }

        public ValueTask<SourceRevertResult> RevertSourceWritesAsync(
            IReadOnlyList<SubjectPropertyChange> written,
            object? revertState,
            CancellationToken cancellationToken)
        {
            Reverted.AddRange(written);
            return new(new SourceRevertResult([], []));
        }
    }
}
