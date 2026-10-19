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
/// <see cref="TransactionValidationMode"/>; the values a commit applies after its check are not validated again, up to the
/// first one that fails to apply.
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
        foreach (var change in changes)
        {
            var errorMessage = Validate(change.Property, change.GetNewValue<object?>(), change.Origin);
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

    private static void ThrowIfInvalid<TProperty>(PropertyReference property, TProperty value, ChangeOrigin origin)
    {
        var errorMessage = Validate(property, value, origin);
        if (errorMessage is not null)
        {
            throw new ValidationException(errorMessage);
        }
    }

    private static string? Validate<TProperty>(PropertyReference property, TProperty value, ChangeOrigin origin)
    {
        var validators = property.Subject.Context.GetServices<IPropertyValidator>();
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
