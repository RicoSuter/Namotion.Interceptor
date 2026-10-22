using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text;
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
/// <see cref="TransactionValidationMode"/>; a write recognized by
/// <see cref="SubjectTransaction.TryConsumeCommitWrite"/> is not validated again.
/// </summary>
[RunsAfter(typeof(SubjectTransactionInterceptor))]
[RunsBefore(typeof(DerivedPropertyChangeHandler), typeof(PropertyChangeInterceptor), typeof(LifecycleInterceptor))]
public class ValidationInterceptor : IWriteInterceptor, ITransactionHandler
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
    public void OnCapturing<TProperty>(SubjectTransaction transaction, PropertyReference property, TProperty newValue, ChangeOrigin origin)
    {
        if (transaction.ValidationMode == TransactionValidationMode.Immediate)
        {
            ThrowIfInvalid(property, newValue, origin);
        }
    }

    /// <inheritdoc />
    public void OnCommitting(SubjectTransaction transaction, ReadOnlySpan<SubjectPropertyChange> changes)
    {
        List<SubjectPropertyChange>? failedChanges = null;
        List<Exception>? errors = null;
        IInterceptorSubjectContext? validatorsContext = null;
        var validators = ImmutableArray<IPropertyValidator>.Empty;
        foreach (ref readonly var change in changes)
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

            var errorMessage = ValidateNewValue(validators, change);
            if (errorMessage is not null)
            {
                (failedChanges ??= []).Add(change);
                (errors ??= []).Add(new ValidationException(errorMessage));
            }
        }

        if (failedChanges is not null)
        {
            throw new SubjectTransactionValidationException(failedChanges, errors!);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="obj"/> is a validation interceptor of the same type. All instances validate
    /// alike, so a context that reaches several registrations, for example its own and its fallback's, uses one.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    public override bool Equals(object? obj)
    {
        return obj is not null && obj.GetType() == GetType();
    }

    /// <inheritdoc />
    public override int GetHashCode() => GetType().GetHashCode();

    /// <summary>
    /// Validates the new value of <paramref name="change"/> with the property type as the type argument for common value
    /// types, so the value is not boxed, and as <c>object</c> otherwise.
    /// </summary>
    private static string? ValidateNewValue(ImmutableArray<IPropertyValidator> validators, in SubjectPropertyChange change)
    {
        var type = change.Property.Metadata.Type;
        if (type.IsValueType)
        {
            string? errorMessage;
            if ((type == typeof(int) && TryValidateAs<int>(validators, change, out errorMessage)) ||
                (type == typeof(long) && TryValidateAs<long>(validators, change, out errorMessage)) ||
                (type == typeof(double) && TryValidateAs<double>(validators, change, out errorMessage)) ||
                (type == typeof(float) && TryValidateAs<float>(validators, change, out errorMessage)) ||
                (type == typeof(decimal) && TryValidateAs<decimal>(validators, change, out errorMessage)) ||
                (type == typeof(bool) && TryValidateAs<bool>(validators, change, out errorMessage)) ||
                (type == typeof(DateTime) && TryValidateAs<DateTime>(validators, change, out errorMessage)) ||
                (type == typeof(DateTimeOffset) && TryValidateAs<DateTimeOffset>(validators, change, out errorMessage)) ||
                (type == typeof(TimeSpan) && TryValidateAs<TimeSpan>(validators, change, out errorMessage)) ||
                (type == typeof(Guid) && TryValidateAs<Guid>(validators, change, out errorMessage)))
            {
                return errorMessage;
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
        out string? errorMessage)
    {
        if (change.TryGetNewValue(out TProperty value))
        {
            errorMessage = Validate(validators, change.Property, value, change.Origin);
            return true;
        }

        errorMessage = null;
        return false;
    }

    private static void ThrowIfInvalid<TProperty>(PropertyReference property, TProperty value, ChangeOrigin origin)
    {
        var errorMessage = Validate(property.Subject.Context.GetServices<IPropertyValidator>(), property, value, origin);
        if (errorMessage is not null)
        {
            throw new ValidationException(errorMessage);
        }
    }

    private static string? Validate<TProperty>(
        ImmutableArray<IPropertyValidator> validators,
        PropertyReference property,
        TProperty value,
        ChangeOrigin origin)
    {
        var validationContext = new PropertyValidationContext<TProperty>(property, value, origin);

        StringBuilder? message = null;
        foreach (var validator in validators)
        {
            foreach (var error in validator.Validate(in validationContext))
            {
                (message ??= new StringBuilder()).Append('\n').Append(error.ErrorMessage);
            }
        }

        return message?.ToString();
    }
}
