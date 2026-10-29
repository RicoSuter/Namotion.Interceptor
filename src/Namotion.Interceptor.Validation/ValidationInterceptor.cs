using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Validates property values with the registered <see cref="IPropertyValidator"/> services. Writes that reach the
/// model are validated before any derived recalculation, change notification or lifecycle handling. Writes captured
/// by a transaction are validated as an <see cref="ITransactionHandler"/>, according to the transaction's
/// <see cref="TransactionCheckMode"/>; a write recognized by
/// <see cref="SubjectTransaction.TryConsumeCommitWrite"/> is not validated again.
/// </summary>
[RunsAfter(typeof(SubjectTransactionInterceptor))]
[RunsBefore(typeof(DerivedPropertyChangeHandler), typeof(PropertyChangeInterceptor), typeof(LifecycleInterceptor))]
public sealed class ValidationInterceptor : IWriteInterceptor, ITransactionHandler
{
    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (!SubjectTransaction.TryConsumeCommitWrite(this, context.Property, context.NewValue))
        {
            ThrowIfInvalid(context.Property, context.NewValue, context.Origin);
        }

        next(ref context);
    }

    /// <inheritdoc />
    public void OnCapturing<TProperty>(in TransactionCaptureContext<TProperty> context)
    {
        if (context.Transaction.CheckMode == TransactionCheckMode.Immediate)
        {
            ThrowIfInvalid(context.Property, context.NewValue, context.Origin);
        }
    }

    /// <inheritdoc />
    public void OnCommitting(in TransactionCheckContext context)
    {
        List<SubjectPropertyChange>? failedChanges = null;
        List<Exception>? errors = null;
        IInterceptorSubjectContext? validatorsContext = null;
        var validators = ImmutableArray<IPropertyValidator>.Empty;
        foreach (ref readonly var change in context.Changes)
        {
            var subjectContext = change.Property.Subject.Context;
            if (!ReferenceEquals(subjectContext, validatorsContext))
            {
                validatorsContext = subjectContext;
                validators = subjectContext.GetServices<IPropertyValidator>();
            }

            if (validators.IsDefaultOrEmpty)
            {
                continue;
            }

            var validationResults = ValidateNewValue(validators, change);
            if (validationResults is not null)
            {
                (failedChanges ??= []).Add(change);
                (errors ??= []).Add(new PropertyValidationException(change.Property, change.GetNewValue<object?>(), validationResults));
            }
        }

        if (failedChanges is not null)
        {
            throw new SubjectTransactionValidationException(failedChanges, errors!);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="obj"/> is a validation interceptor. All instances validate
    /// alike, so a context that reaches several registrations, for example its own and its fallback's, uses one.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    public override bool Equals(object? obj)
    {
        return obj is ValidationInterceptor;
    }

    /// <inheritdoc />
    public override int GetHashCode() => typeof(ValidationInterceptor).GetHashCode();

    /// <summary>
    /// Validates the new value of <paramref name="change"/> with the property type as the type argument for common
    /// value types, so the value is not boxed, and as <c>object</c> otherwise.
    /// </summary>
    private static List<ValidationResult>? ValidateNewValue(ImmutableArray<IPropertyValidator> validators, in SubjectPropertyChange change)
    {
        var type = change.Property.Metadata.Type;
        if (type.IsValueType)
        {
            List<ValidationResult>? validationResults;
            if ((type == typeof(int) && TryValidateAs<int>(validators, change, out validationResults)) ||
                (type == typeof(long) && TryValidateAs<long>(validators, change, out validationResults)) ||
                (type == typeof(double) && TryValidateAs<double>(validators, change, out validationResults)) ||
                (type == typeof(float) && TryValidateAs<float>(validators, change, out validationResults)) ||
                (type == typeof(decimal) && TryValidateAs<decimal>(validators, change, out validationResults)) ||
                (type == typeof(bool) && TryValidateAs<bool>(validators, change, out validationResults)) ||
                (type == typeof(DateTime) && TryValidateAs<DateTime>(validators, change, out validationResults)) ||
                (type == typeof(DateTimeOffset) && TryValidateAs<DateTimeOffset>(validators, change, out validationResults)) ||
                (type == typeof(TimeSpan) && TryValidateAs<TimeSpan>(validators, change, out validationResults)) ||
                (type == typeof(Guid) && TryValidateAs<Guid>(validators, change, out validationResults)))
            {
                return validationResults;
            }
        }

        return Validate(validators, change.Property, change.GetNewValue<object?>(), change.Origin);
    }

    /// <summary>
    /// Validates the new value of <paramref name="change"/> as <typeparamref name="TProperty"/>. Returns false without
    /// validating when the new value is not a <typeparamref name="TProperty"/>: null, or a value of another type, which
    /// only an untyped write can store.
    /// </summary>
    private static bool TryValidateAs<TProperty>(
        ImmutableArray<IPropertyValidator> validators,
        in SubjectPropertyChange change,
        out List<ValidationResult>? validationResults)
    {
        if (change.TryGetNewValue(out TProperty value))
        {
            validationResults = Validate(validators, change.Property, value, change.Origin);
            return true;
        }

        validationResults = null;
        return false;
    }

    private static void ThrowIfInvalid<TProperty>(PropertyReference property, TProperty value, ChangeOrigin origin)
    {
        var validationResults = Validate(property.Subject.Context.GetServices<IPropertyValidator>(), property, value, origin);
        if (validationResults is not null)
        {
            throw new PropertyValidationException(property, value, validationResults);
        }
    }

    /// <summary>
    /// Returns the results of every validator for <paramref name="value"/>, or null when the value is valid.
    /// </summary>
    private static List<ValidationResult>? Validate<TProperty>(
        ImmutableArray<IPropertyValidator> validators,
        PropertyReference property,
        TProperty value,
        ChangeOrigin origin)
    {
        var validationContext = new PropertyValidationContext<TProperty>(property, value, origin);

        List<ValidationResult>? validationResults = null;
        foreach (var validator in validators)
        {
            foreach (var validationResult in validator.Validate(in validationContext))
            {
                (validationResults ??= []).Add(validationResult);
            }
        }

        return validationResults;
    }
}
