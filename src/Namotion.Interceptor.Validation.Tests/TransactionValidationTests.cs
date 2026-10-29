using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDeferredAndDependencyIsWrittenAfterDependent_ThenCommitSucceeds(bool hookWritesAnotherValueAtApply)
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context);

        // Act
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
        motor.MotorSpeed = 150;
        motor.MaxAllowedSpeed = 200;
        if (hookWritesAnotherValueAtApply)
        {
            // The hook's write of another value to the applied property does not get the checked apply rejected.
            motor.SpeedToWriteOnSpeedChanging = 10;
        }

        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(200, motor.MaxAllowedSpeed);
        Assert.Equal(150, motor.MotorSpeed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WhenFinalSetIsInvalid_ThenCommitReportsEveryInvalidValueInOrderBeforeAnythingIsWritten(int invalidCount)
    {
        // Arrange
        var context = CreateContext();
        var motors = new[] { new Motor(context), new Motor(context) };
        var writer = new TestTransactionWriter();
        context.AddService<ITransactionWriter>(writer);

        // Act
        SubjectTransactionValidationException exception;
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred))
        {
            for (var index = 0; index < invalidCount; index++)
            {
                motors[index].MotorSpeed = 150 + index * 10;
            }

            exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Equal(motors.Take(invalidCount), exception.FailedChanges.Select(change => (Motor)change.Property.Subject));
        Assert.Equal(invalidCount, exception.Errors.Count);
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

        if (invalidCount == 1)
        {
            Assert.Same(exception.Errors[0], exception.InnerException);
        }
        else
        {
            Assert.IsType<AggregateException>(exception.InnerException);
        }

        Assert.Empty(exception.AppliedChanges);
        Assert.Equal(0, writer.WriteCount);
        Assert.All(motors, motor => Assert.Equal(0, motor.MotorSpeed));
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
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
        motor.MotorSpeed = 150;
        motor.MaxAllowedSpeed = 200;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(150, motor.MotorSpeed);
        Assert.Equal(200, motor.MaxAllowedSpeed);
        Assert.Equal([nameof(Motor.MotorSpeed), nameof(Motor.MaxAllowedSpeed)], validator.Calls.Select(call => call.Property));
    }

    [Fact]
    public async Task WhenValidationIsRegisteredOnTransactionContextAfterBegin_ThenSubjectContextValidationStillChecksTheSet()
    {
        // Arrange
        var transactionContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subjectContext = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithService<IPropertyValidator>(() => new MotorSpeedValidator());
        subjectContext.AddFallbackContext(transactionContext);
        var good = new Motor(subjectContext);
        var bad = new Motor(subjectContext);
        using var transaction = await transactionContext.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
        transactionContext.WithPropertyValidation();
        good.MotorSpeed = 10;
        bad.MotorSpeed = 150;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        transaction.Dispose();

        // Assert
        Assert.Same(bad, Assert.Single(exception.FailedChanges).Property.Subject);
        Assert.Equal(0, good.MotorSpeed);
        Assert.Equal(0, bad.MotorSpeed);
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
            TransactionFailureHandling.BestEffort, checkMode: TransactionCheckMode.Deferred);
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
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
        motor.MotorSpeed = 150;

        // Act & Assert
        await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task WhenUnchangedValueBecomesInvalidWhileSourcesAreWritten_ThenPreApplyCheckRejectsAndRevertsSources()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MotorSpeed = 90 };
        var writer = new TestTransactionWriter(updateDuringWrite: () => motor.MotorSpeed = 99);
        context.AddService<ITransactionWriter>(writer);
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
        motor.MaxAllowedSpeed = 95;
        motor.MotorSpeed = 90;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        var error = Assert.IsType<PropertyValidationException>(Assert.Single(exception.Errors));
        Assert.Equal(nameof(Motor.MotorSpeed), error.Property.Name);
        Assert.Equal(100, motor.MaxAllowedSpeed);
        Assert.Equal(99, motor.MotorSpeed);
        Assert.Equal(nameof(Motor.MaxAllowedSpeed), Assert.Single(writer.Reverted).Property.Name);
    }

    public enum OriginRule
    {
        IgnoresOrigin,
        SkipsNonConfirmed,
        SkipsNonLocal
    }

    [Theory]
    [InlineData(OriginRule.IgnoresOrigin, true)]
    [InlineData(OriginRule.SkipsNonConfirmed, true)]
    [InlineData(OriginRule.SkipsNonLocal, false)]
    public async Task WhenSourceAcceptedTheValueAndTheLimitDropsMeanwhile_ThenPreApplyCheckGetsConfirmedAndFollowsTheValidator(
        OriginRule rule, bool rejects)
    {
        // Arrange
        Func<ChangeOrigin, bool> skips = rule switch
        {
            OriginRule.SkipsNonConfirmed => origin => origin.Kind != ChangeOriginKind.Confirmed,
            OriginRule.SkipsNonLocal => origin => origin.Kind != ChangeOriginKind.Local,
            _ => _ => false
        };
        var validator = new OriginAwareMotorSpeedValidator(skips);
        var context = CreateContext(validator);
        var motor = new Motor(context) { MaxAllowedSpeed = 200 };
        var writer = new TestTransactionWriter(updateDuringWrite: () => motor.MaxAllowedSpeed = 100, confirmWrites: true);
        context.AddService<ITransactionWriter>(writer);
        validator.Origins.Clear();
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
        motor.MotorSpeed = 150;

        // Act
        var exception = await Record.ExceptionAsync(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        Assert.Equal([ChangeOriginKind.Local, ChangeOriginKind.Confirmed], validator.Origins.Select(origin => origin.Kind));
        Assert.Same(writer, validator.Origins[1].Source);
        Assert.Equal(100, motor.MaxAllowedSpeed);
        if (rejects)
        {
            var failure = Assert.IsType<SubjectTransactionException>(exception);
            Assert.Equal(nameof(Motor.MotorSpeed), Assert.Single(failure.FailedChanges).Property.Name);
            Assert.Equal(0, motor.MotorSpeed);
            Assert.Single(writer.Reverted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal(150, motor.MotorSpeed);
            Assert.Empty(writer.Reverted);
        }
    }

    [Fact]
    public void WhenValidationExceptionIsCreatedWithoutErrors_ThenItThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => new SubjectTransactionValidationException([], []));
        Assert.Equal("errors", exception.ParamName);
    }

    [Theory]
    [InlineData(TransactionCheckMode.Immediate, true, 3)]
    [InlineData(TransactionCheckMode.Deferred, true, 2)]
    [InlineData(TransactionCheckMode.Immediate, false, 2)]
    [InlineData(TransactionCheckMode.Deferred, false, 1)]
    public async Task WhenValidValueIsCommitted_ThenValidatorIsCalledOncePerCaptureAndCheck(
        TransactionCheckMode checkMode, bool withWriter, int expectedCallCount)
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
            TransactionFailureHandling.Rollback, checkMode: checkMode);
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
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
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
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
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

    [Theory]
    [InlineData(TransactionConflictBehavior.FailOnConflict, false)]
    [InlineData(TransactionConflictBehavior.FailOnConflict, true)]
    [InlineData(TransactionConflictBehavior.Ignore, false)]
    public async Task WhenLandedValueOfUnchangedWriteChangesBeforeCommit_ThenCommitFailsInsteadOfLandingAnInvalidSet(
        TransactionConflictBehavior conflictBehavior, bool withWriter)
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
            conflictBehavior: conflictBehavior,
            checkMode: TransactionCheckMode.Deferred);
        motor.MaxAllowedSpeed = 100;
        motor.MotorSpeed = 90;
        await RunWithoutAsyncLocalFlowAsync(() => motor.MaxAllowedSpeed = 60);

        // Act
        var exception = await Record.ExceptionAsync(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        // With conflict detection the unchanged write conflicts, without it the commit check reads the landed value.
        if (conflictBehavior == TransactionConflictBehavior.FailOnConflict)
        {
            var conflict = Assert.IsType<SubjectTransactionConflictException>(exception);
            Assert.Equal(nameof(Motor.MaxAllowedSpeed), Assert.Single(conflict.ConflictingProperties).Name);
        }
        else
        {
            var rejection = Assert.IsType<SubjectTransactionValidationException>(exception);
            Assert.Equal(nameof(Motor.MotorSpeed), Assert.Single(rejection.FailedChanges).Property.Name);
        }

        Assert.Equal(0, writer.WriteCount);
        int landedSpeed = 0;
        await RunWithoutAsyncLocalFlowAsync(() => landedSpeed = motor.MotorSpeed);
        Assert.Equal(50, landedSpeed);
    }

    [Fact]
    public async Task WhenDeferredUnchangedValueIsInvalidAgainstFinalSet_ThenCommitRejectsItUntilANewValueIsWritten()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MotorSpeed = 50 };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
        motor.MaxAllowedSpeed = 40;
        motor.MotorSpeed = 50;

        // Act
        var exception = await Assert.ThrowsAsync<SubjectTransactionValidationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        var pendingChangesAfterRejection = transaction.GetPendingChanges();
        motor.MotorSpeed = 30;
        var pendingChanges = transaction.GetPendingChanges();
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        var failedChange = Assert.Single(exception.FailedChanges);
        Assert.Equal(nameof(Motor.MotorSpeed), failedChange.Property.Name);
        Assert.Equal(50, failedChange.GetOldValue<int>());
        Assert.Equal(50, failedChange.GetNewValue<int>());
        Assert.Equal([nameof(Motor.MaxAllowedSpeed)], pendingChangesAfterRejection.Select(change => change.Property.Name));
        Assert.Equal(
            [nameof(Motor.MaxAllowedSpeed), nameof(Motor.MotorSpeed)],
            pendingChanges.Select(change => change.Property.Name));
        var speedChange = pendingChanges[1];
        Assert.Equal(50, speedChange.GetOldValue<int>());
        Assert.Equal(30, speedChange.GetNewValue<int>());
        Assert.Equal(30, motor.MotorSpeed);
        Assert.Equal(40, motor.MaxAllowedSpeed);
    }

    [Fact]
    public async Task WhenInterceptorBeforeTransactionReturnsEqualNewInstance_ThenDeferredCommitDoesNotValidateTheApplyAgain()
    {
        // Arrange
        var context = CreateContext(new TextLengthValidator());
        context.AddService(new CopyingStringInterceptor());
        var field = new TextField(context) { MaxLength = 3 };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, checkMode: TransactionCheckMode.Deferred);
        field.Text = "abcdef";
        field.MaxLength = 10;

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("abcdef", field.Text);
        Assert.Equal(10, field.MaxLength);
    }

    [Theory]
    [InlineData(false, false, TransactionConflictBehavior.FailOnConflict)]
    [InlineData(false, true, TransactionConflictBehavior.Ignore)]
    [InlineData(true, false, TransactionConflictBehavior.Ignore)]
    [InlineData(true, true, TransactionConflictBehavior.FailOnConflict)]
    public async Task WhenWrittenSubjectLeavesTheTransactionContextBeforeDeferredCommit_ThenCommitConflictsAndWritesNothing(
        bool movesToAnotherContext, bool withWriter, TransactionConflictBehavior conflictBehavior)
    {
        // Arrange
        var context = CreateContext();
        var writer = new TestTransactionWriter();
        if (withWriter)
        {
            context.AddService<ITransactionWriter>(writer);
        }

        var holder = new MotorHolder(context);
        // Without validators, so the commit check passes and only the conflict detection rejects the move.
        var otherHolder = new MotorHolder(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions());
        var motor = new Motor();
        holder.Motor = motor;

        SubjectTransactionConflictException exception;
        SubjectTransactionConflictException retryException;
        using (var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, conflictBehavior: conflictBehavior, checkMode: TransactionCheckMode.Deferred))
        {
            motor.MaxAllowedSpeed = 100; // unchanged, a check-only entry with conflict detection
            motor.MotorSpeed = 500;
            await RunWithoutAsyncLocalFlowAsync(() =>
            {
                holder.Motor = null;
                if (movesToAnotherContext)
                {
                    otherHolder.Motor = motor;
                }
            });

            // Act
            exception = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
            retryException = await Assert.ThrowsAsync<SubjectTransactionConflictException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        string[] expectedConflicts = conflictBehavior == TransactionConflictBehavior.FailOnConflict
            ? [nameof(Motor.MotorSpeed), nameof(Motor.MaxAllowedSpeed)]
            : [nameof(Motor.MotorSpeed)];
        Assert.Equal(expectedConflicts, exception.ConflictingProperties.Select(property => property.Name));
        Assert.Equal(expectedConflicts, retryException.ConflictingProperties.Select(property => property.Name));
        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(0, motor.MotorSpeed);
    }

    [Theory]
    [InlineData(false, TransactionConflictBehavior.FailOnConflict)]
    [InlineData(false, TransactionConflictBehavior.Ignore)]
    [InlineData(true, TransactionConflictBehavior.FailOnConflict)]
    [InlineData(true, TransactionConflictBehavior.Ignore)]
    public async Task WhenWrittenSubjectMovesToAnotherHolderInTheContextBeforeDeferredCommit_ThenCommitSucceeds(
        bool withWriter, TransactionConflictBehavior conflictBehavior)
    {
        // Arrange
        var context = CreateContext();
        var writer = new TestTransactionWriter();
        if (withWriter)
        {
            context.AddService<ITransactionWriter>(writer);
        }

        var holder = new MotorHolder(context);
        var otherHolder = new MotorHolder(context);
        var motor = new Motor();
        holder.Motor = motor;

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, conflictBehavior: conflictBehavior, checkMode: TransactionCheckMode.Deferred);
        motor.MaxAllowedSpeed = 100; // unchanged, a check-only entry with conflict detection
        motor.MotorSpeed = 50;
        await RunWithoutAsyncLocalFlowAsync(() =>
        {
            holder.Motor = null;
            otherHolder.Motor = motor;
        });

        // Act
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal(50, motor.MotorSpeed);
        Assert.Equal(withWriter ? 1 : 0, writer.WriteCount);
    }

    [Fact]
    public async Task WhenIgnoreCommitRollsBackAfterAConcurrentWrite_ThenTheRestoredValueIsTheValidLandedValue()
    {
        // Arrange
        var context = CreateContext();
        var motor = new Motor(context) { MotorSpeed = 80 };
        var failingMotor = new Motor(context) { IncrementSpeedOnWrite = true };
        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.Rollback, TransactionLocking.Optimistic, conflictBehavior: TransactionConflictBehavior.Ignore);
        motor.MotorSpeed = 40;
        failingMotor.MotorSpeed = 99;
        await RunWithoutAsyncLocalFlowAsync(() =>
        {
            motor.MaxAllowedSpeed = 60;
            motor.MotorSpeed = 50;
        });

        // Act
        await Assert.ThrowsAsync<SubjectTransactionException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());

        // Assert
        // The restore is part of the checked set, so it is not validated; restoring the speed of 80 captured at the
        // first write would exceed the limit of 60.
        transaction.Dispose();
        Assert.Equal(60, motor.MaxAllowedSpeed);
        Assert.Equal(50, motor.MotorSpeed);
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
    /// Rejects a Text longer than MaxLength, read through the getter like <see cref="MotorSpeedValidator"/>.
    /// </summary>
    private sealed class TextLengthValidator : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            if (context.Property.Name == nameof(TextField.Text) &&
                context.Value is string text &&
                context.Property.Subject is TextField field &&
                text.Length > field.MaxLength)
            {
                return [new ValidationResult($"Text length {text.Length} exceeds MaxLength {field.MaxLength}.")];
            }

            return [];
        }
    }

    /// <summary>
    /// Replaces every string written with an equal, distinct instance before the transaction sees it, like a
    /// normalizing interceptor whose result is already normalized.
    /// </summary>
    [RunsBefore(typeof(SubjectTransactionInterceptor))]
    private sealed class CopyingStringInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.NewValue is string text)
            {
                context.NewValue = (TProperty)(object)new string(text.AsSpan());
            }

            next(ref context);
        }
    }

    /// <summary>
    /// Checks MotorSpeed against MaxAllowedSpeed like <see cref="MotorSpeedValidator"/>, records the origin of every
    /// MotorSpeed value it receives, and skips the values whose origin <paramref name="skips"/> accepts.
    /// </summary>
    private sealed class OriginAwareMotorSpeedValidator(Func<ChangeOrigin, bool> skips) : IPropertyValidator
    {
        private readonly MotorSpeedValidator _inner = new();

        public List<ChangeOrigin> Origins { get; } = [];

        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            if (context.Property.Name != nameof(Motor.MotorSpeed))
            {
                return [];
            }

            Origins.Add(context.Origin);
            return skips(context.Origin) ? [] : _inner.Validate(in context);
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
    /// the source write is in flight, like an inbound source update, and optionally marks the accepted snapshot slots
    /// as confirmed by this writer in place, as the writer contract requires.
    /// </summary>
    private sealed class TestTransactionWriter(Action? updateDuringWrite = null, bool confirmWrites = false) : ITransactionWriter
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

            if (confirmWrites)
            {
                var span = changes.Span;
                for (var index = 0; index < span.Length; index++)
                {
                    span[index] = span[index].WithOrigin(ChangeOrigin.Confirmed(this));
                }
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
