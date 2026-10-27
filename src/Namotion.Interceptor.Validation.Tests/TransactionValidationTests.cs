using System.ComponentModel.DataAnnotations;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;
using Namotion.Interceptor.Validation.Tests.Models;

namespace Namotion.Interceptor.Validation.Tests;

public class TransactionValidationTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return CreateContext(new MotorSpeedValidator());
    }

    private static IInterceptorSubjectContext CreateContext(IPropertyValidator validator)
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => validator);
    }

    /// <summary>
    /// Creates a motor whose own context registers the validation and reaches the transaction interceptor through a
    /// fallback context without validation, and returns that fallback context to begin transactions on.
    /// </summary>
    private static (IInterceptorSubjectContext TransactionContext, Motor Motor) CreateSubjectValidatedThroughFallback()
    {
        var transactionContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subjectContext = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => new MotorSpeedValidator());
        subjectContext.AddFallbackContext(transactionContext);
        return (transactionContext, new Motor(subjectContext));
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
        var writer = new TestTransactionWriter();
        context.AddService<ITransactionWriter>(writer);

        // Act
        SubjectTransactionValidationException exception;
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred))
        {
            motor.MotorSpeed = 150;
            exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        var failed = Assert.Single(exception.FailedChanges);
        Assert.Equal(nameof(Motor.MotorSpeed), failed.Property.Name);
        Assert.IsType<PropertyValidationException>(Assert.Single(exception.Errors));
        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(0, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenSeveralValuesAreInvalid_ThenAllAreReportedInOrder()
    {
        // Arrange
        var context = CreateContext();
        var firstMotor = new Motor(context);
        var secondMotor = new Motor(context);
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        firstMotor.MotorSpeed = 150;
        secondMotor.MotorSpeed = 160;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(2, exception.FailedChanges.Count);
        Assert.Equal(2, exception.Errors.Count);
        Assert.Same(firstMotor, exception.FailedChanges[0].Property.Subject);
        Assert.Same(secondMotor, exception.FailedChanges[1].Property.Subject);
        for (var i = 0; i < exception.FailedChanges.Count; i++)
        {
            var failedChange = exception.FailedChanges[i];
            var speed = failedChange.GetNewValue<int>();
            var error = Assert.IsType<PropertyValidationException>(exception.Errors[i]);
            Assert.Equal(failedChange.Property, error.Property);
            Assert.Equal(speed, error.Value);
            Assert.Single(error.ValidationResults);
            Assert.Contains($"MotorSpeed {speed} ", error.Message);
        }

        Assert.IsType<AggregateException>(exception.InnerException);
    }

    [Fact]
    public async Task WhenImmediateAndWriteIsInvalid_ThenSetterThrows()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);

        // Act & Assert
        Assert.Throws<PropertyValidationException>(() => motor.MotorSpeed = 150);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenDeferredAndSubjectContextValidatesThroughFallback_ThenCommitThrowsBeforeAnythingIsWritten()
    {
        // Arrange
        var (transactionContext, motor) = CreateSubjectValidatedThroughFallback();
        var writer = new TestTransactionWriter();
        transactionContext.AddService<ITransactionWriter>(writer);

        // Act
        SubjectTransactionValidationException exception;
        using (var transaction = await transactionContext.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred))
        {
            motor.MotorSpeed = 150;
            exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Equal(nameof(Motor.MotorSpeed), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(0, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenSubjectContextAndFallbackBothRegisterValidation_ThenCommitIsValidatedOnce()
    {
        // Arrange
        var validator = new RecordingValidator(new MotorSpeedValidator());
        var transactionContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions()
            .WithPropertyValidation();
        var subjectContext = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => validator);
        subjectContext.AddFallbackContext(transactionContext);
        var motor = new Motor(subjectContext);
        validator.Calls.Clear();

        // Act
        using var transaction = await transactionContext.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 150;
        motor.MaxAllowedSpeed = 200;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(150, motor.MotorSpeed);
        Assert.Equal(200, motor.MaxAllowedSpeed);
        Assert.Equal([nameof(Motor.MotorSpeed), nameof(Motor.MaxAllowedSpeed)], validator.Calls.Select(call => call.Property));
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
        Assert.IsType<PropertyValidationException>(Assert.Single(exception.Errors));
        Assert.Equal(0, motor.MotorSpeed);
    }

    [Fact]
    public async Task WhenRollbackRestoresAppliedValues_ThenRestoresAreNotValidated()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MaxAllowedSpeed = 200, MotorSpeed = 150 };
        var failingMotor = new Motor(context) { IncrementSpeedOnWrite = true };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        motor.MaxAllowedSpeed = 100;
        motor.MotorSpeed = 50;
        failingMotor.MotorSpeed = 99;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        // Reverse-order rollback restores MotorSpeed 150 while MaxAllowedSpeed is still 100.
        Assert.IsType<PropertyValidationException>(Assert.Single(exception.Errors));
        Assert.Equal(
            [motor, motor, failingMotor],
            exception.FailedChanges.Select(change => change.Property.Subject));
        Assert.Equal(200, motor.MaxAllowedSpeed);
        Assert.Equal(150, motor.MotorSpeed);
        Assert.Equal(0, failingMotor.MotorSpeed);
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
    public async Task WhenLimitIsLoweredWhileSourcesAreWritten_ThenPreApplyCheckRejectsAndRevertsSources()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MaxAllowedSpeed = 200 };
        var writer = new TestTransactionWriter(updateDuringWrite: () => motor.MaxAllowedSpeed = 100);
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
    public void WhenValidationExceptionIsCreatedWithoutErrors_ThenItThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => new SubjectTransactionValidationException([], []));
        Assert.Equal("errors", exception.ParamName);
    }

    [Theory]
    [InlineData(TransactionValidationMode.Immediate, true, 3)]
    [InlineData(TransactionValidationMode.Deferred, true, 2)]
    [InlineData(TransactionValidationMode.Immediate, false, 2)]
    [InlineData(TransactionValidationMode.Deferred, false, 1)]
    public async Task WhenValidValueIsCommitted_ThenValidatorIsCalledOncePerCaptureAndCheck(
        TransactionValidationMode validationMode, bool withWriter, int expectedCallCount)
    {
        // Arrange
        var validator = new RecordingValidator();
        var context = CreateContext(validator);
        if (withWriter)
        {
            context.AddService<ITransactionWriter>(new TestTransactionWriter());
        }

        var motor = new Motor(context);
        validator.Calls.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: validationMode);
        motor.MotorSpeed = 50;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedCallCount, validator.Calls.Count);
        Assert.All(validator.Calls, call =>
        {
            Assert.Equal(nameof(Motor.MotorSpeed), call.Property);
            Assert.Equal(typeof(int), call.TypeArgument);
            Assert.Equal(50, call.Value);
        });
        Assert.Equal(50, motor.MotorSpeed);
    }

    [Theory]
    [InlineData(5L)]
    [InlineData(null)]
    public async Task WhenUntypedWriteStoresValueOfAnotherType_ThenCommitCheckValidatesItAsObject(object? value)
    {
        // Arrange
        var validator = new RecordingValidator();
        var context = CreateContext(validator);
        var motor = new Motor(context);
        validator.Calls.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        new PropertyReference(motor, nameof(Motor.MotorSpeed)).SetPropertyValueWithInterception(
            value, motor.MotorSpeed, static (_, _) => { });
        // Applying null or a value of another type to the int property fails; only the commit check's call matters here.
        await Record.ExceptionAsync(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        var commitCheckCall = validator.Calls[0];
        Assert.Equal(nameof(Motor.MotorSpeed), commitCheckCall.Property);
        Assert.Equal(typeof(object), commitCheckCall.TypeArgument);
        Assert.Equal(value, commitCheckCall.Value);
    }

    [Fact]
    public async Task WhenPropertyTypeIsNotACommonValueType_ThenChecksValidateTheValueAsObject()
    {
        // Arrange
        var validator = new RecordingValidator();
        var context = CreateContext(validator);
        var gearbox = new Gearbox(context);
        validator.Calls.Clear();

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        gearbox.Gear = 3;
        gearbox.Label = "Sport";
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        (string, Type, object?)[] checkCalls =
        [
            (nameof(Gearbox.Gear), typeof(object), 3),
            (nameof(Gearbox.Label), typeof(object), "Sport")
        ];
        Assert.Equal(checkCalls, validator.Calls);
        Assert.Equal(3, gearbox.Gear);
        Assert.Equal("Sport", gearbox.Label);
    }

    [Theory]
    [InlineData(TransactionFailureHandling.BestEffort)]
    [InlineData(TransactionFailureHandling.Rollback)]
    public async Task WhenDerivedPropertyBecomesInvalid_ThenBothChecksPassAndApplyFailsAfterSourceWrite(
        TransactionFailureHandling failureHandling)
    {
        // Arrange
        var log = new List<string>();
        var context = CreateContext(new PowerLimitValidator(log));
        var writer = new TestTransactionWriter(updateDuringWrite: () => log.Add("source write"));
        context.AddService<ITransactionWriter>(writer);
        var motor = new PoweredMotor(context);
        _ = motor.Power;
        log.Clear();
        using var transaction = await context.BeginTransactionAsync(failureHandling);
        motor.MotorSpeed = 200;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(
            [
                nameof(PoweredMotor.MotorSpeed), // capture
                nameof(PoweredMotor.MotorSpeed), // commit check
                "source write",
                nameof(PoweredMotor.MotorSpeed), // pre-apply check
                nameof(PoweredMotor.Power) // recalculation while the commit applies MotorSpeed
            ],
            log);
        Assert.Equal(1, writer.WriteCount);
        Assert.Equal(nameof(PoweredMotor.MotorSpeed), Assert.Single(exception.FailedChanges).Property.Name);
        Assert.Contains("Power 2000 exceeds", Assert.IsType<PropertyValidationException>(Assert.Single(exception.Errors)).Message);
    }

    [Fact]
    public async Task WhenSubjectCreatedWithoutContextIsAttachedInTransaction_ThenItsValuesAreNotValidated()
    {
        // Arrange
        var validator = new RecordingValidator(new MotorSpeedValidator());
        var context = CreateContext(validator);
        var holder = new MotorHolder(context);
        var motor = new Motor { MotorSpeed = 500 };
        validator.Calls.Clear();

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback))
        {
            holder.Motor = motor;
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Same(motor, holder.Motor);
        Assert.Equal(500, motor.MotorSpeed);
        // Only the captured reference write is validated, at capture and in the commit check.
        Assert.Equal([nameof(MotorHolder.Motor), nameof(MotorHolder.Motor)], validator.Calls.Select(call => call.Property));

        // The attached motor now uses the context, so a later write is validated.
        Assert.Throws<PropertyValidationException>(() => motor.MotorSpeed = 600);
    }

    [Fact]
    public async Task WhenDeferredAndUnchangedValueIsInvalidAgainstFinalSet_ThenCommitRejectsIt()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MotorSpeed = 50 };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MaxAllowedSpeed = 40;
        motor.MotorSpeed = 50;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        var failedChange = Assert.Single(exception.FailedChanges);
        Assert.Equal(nameof(Motor.MotorSpeed), failedChange.Property.Name);
        Assert.Equal(50, failedChange.GetOldValue<int>());
        Assert.Equal(50, failedChange.GetNewValue<int>());
        Assert.Equal([nameof(Motor.MaxAllowedSpeed)], transaction.GetPendingChanges().Select(change => change.Property.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDeferredAndUnchangedValueIsValid_ThenItIsNeitherWrittenNorPublished(bool withWriter)
    {
        // Arrange
        var context = CreateContext();
        var writer = new TestTransactionWriter();
        if (withWriter)
        {
            context.AddService<ITransactionWriter>(writer);
        }

        var motor = new Motor(context) { MotorSpeed = 50 };
        var published = new List<string>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => published.Add(change.Property.Name));
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MotorSpeed = 50;
        motor.MaxAllowedSpeed = 80;

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(50, motor.MotorSpeed);
        Assert.Equal(80, motor.MaxAllowedSpeed);
        Assert.Equal([nameof(Motor.MaxAllowedSpeed)], published);
        Assert.Equal(withWriter ? [nameof(Motor.MaxAllowedSpeed)] : [], writer.Written.Select(change => change.Property.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenLandedValueOfUnchangedWriteChangesBeforeCommit_ThenCommitConflictsInsteadOfLandingAnInvalidSet(bool withWriter)
    {
        // Arrange
        var context = CreateContext();
        var writer = new TestTransactionWriter();
        if (withWriter)
        {
            context.AddService<ITransactionWriter>(writer);
        }

        var motor = new Motor(context) { MotorSpeed = 50 };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.FailOnConflict,
            validationMode: TransactionValidationMode.Deferred);
        motor.MaxAllowedSpeed = 100;
        motor.MotorSpeed = 90;
        await RunWithoutAsyncLocalFlowAsync(() => motor.MaxAllowedSpeed = 60);

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(nameof(Motor.MaxAllowedSpeed), Assert.Single(exception.ConflictingProperties).Name);
        Assert.Equal(0, writer.WriteCount);
        int landedSpeed = 0;
        await RunWithoutAsyncLocalFlowAsync(() => landedSpeed = motor.MotorSpeed);
        Assert.Equal(50, landedSpeed);
    }

    [Fact]
    public async Task WhenDeferredWithIgnoreAndLandedValueOfUnchangedWriteChanges_ThenCommitCheckReadsTheLandedValue()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MotorSpeed = 50 };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback,
            TransactionLocking.Optimistic,
            conflictBehavior: TransactionConflictBehavior.Ignore,
            validationMode: TransactionValidationMode.Deferred);
        motor.MaxAllowedSpeed = 100;
        motor.MotorSpeed = 90;
        await RunWithoutAsyncLocalFlowAsync(() => motor.MaxAllowedSpeed = 60);

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal(nameof(Motor.MotorSpeed), Assert.Single(exception.FailedChanges).Property.Name);
    }

    [Fact]
    public async Task WhenDeferredUnchangedValueIsRejectedAndWrittenAgain_ThenNewValueIsCommitted()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MotorSpeed = 50 };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, validationMode: TransactionValidationMode.Deferred);
        motor.MaxAllowedSpeed = 40;
        motor.MotorSpeed = 50;
        await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Act
        motor.MotorSpeed = 30;
        var pendingChanges = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(
            [nameof(Motor.MaxAllowedSpeed), nameof(Motor.MotorSpeed)],
            pendingChanges.Select(change => change.Property.Name));
        var speedChange = pendingChanges[1];
        Assert.Equal(50, speedChange.GetOldValue<int>());
        Assert.Equal(30, speedChange.GetNewValue<int>());
        Assert.Equal(30, motor.MotorSpeed);
        Assert.Equal(40, motor.MaxAllowedSpeed);
    }

    private static async Task RunWithoutAsyncLocalFlowAsync(Action action, CancellationToken cancellationToken = default)
    {
        Task task;
        var flowControl = ExecutionContext.SuppressFlow();
        try
        {
            task = Task.Run(action, cancellationToken);
        }
        finally
        {
            flowControl.Undo();
        }

        await task;
    }

    /// <summary>
    /// Rejects a MotorSpeed above MaxAllowedSpeed. Reads MaxAllowedSpeed through the getter, so inside a transaction it
    /// sees the transaction's value.
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
    /// Records the property, type argument and value of every call and delegates the validation to
    /// <paramref name="inner"/>, or accepts every value without one.
    /// </summary>
    private sealed class RecordingValidator(IPropertyValidator? inner = null) : IPropertyValidator
    {
        public List<(string Property, Type TypeArgument, object? Value)> Calls { get; } = [];

        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            Calls.Add((context.Property.Name, typeof(TProperty), context.Value));
            return inner?.Validate(in context) ?? [];
        }
    }

    /// <summary>
    /// Logs the name of every property it validates and rejects a Power above 1000.
    /// </summary>
    private sealed class PowerLimitValidator(List<string> log) : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            log.Add(context.Property.Name);
            if (context.Property.Name == nameof(PoweredMotor.Power) && context.Value is int power && power > 1000)
            {
                return [new ValidationResult($"Power {power} exceeds 1000.")];
            }

            return [];
        }
    }

    /// <summary>
    /// Accepts every change and records its calls. Optionally runs an update from a flow without the transaction while
    /// the source write is in flight, like an inbound source update.
    /// </summary>
    private sealed class TestTransactionWriter(Action? updateDuringWrite = null) : ITransactionWriter
    {
        public int WriteCount { get; private set; }

        public List<SubjectPropertyChange> Written { get; } = [];

        public List<SubjectPropertyChange> Reverted { get; } = [];

        public async ValueTask<SourceWriteResult> WriteToSourcesAsync(
            Memory<SubjectPropertyChange> changes,
            TransactionRequirement requirement,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            if (updateDuringWrite is { } update)
            {
                await RunWithoutAsyncLocalFlowAsync(update, cancellationToken);
            }

            Written.AddRange(changes.ToArray());
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
