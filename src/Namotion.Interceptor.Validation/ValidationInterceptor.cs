using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<Type, ITypedValidation> TypedValidations = new();

    static ValidationInterceptor()
    {
        // An untyped write registers only object, so the common value types are registered up front: their commit
        // checks then use the property type whichever writes ran before.
        TypedValidation<int>.Register();
        TypedValidation<long>.Register();
        TypedValidation<double>.Register();
        TypedValidation<float>.Register();
        TypedValidation<decimal>.Register();
        TypedValidation<bool>.Register();
        TypedValidation<DateTime>.Register();
        TypedValidation<DateTimeOffset>.Register();
        TypedValidation<TimeSpan>.Register();
        TypedValidation<Guid>.Register();
    }

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
        // Registers the typed commit check for TProperty, in both modes, before the change reaches OnCommitting.
        TypedValidation<TProperty>.Register();

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
        Type? validationType = null;
        ITypedValidation? typedValidation = null;
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

            var propertyType = change.Property.Metadata.Type;
            if (!ReferenceEquals(propertyType, validationType))
            {
                validationType = propertyType;
                typedValidation = TypedValidations.TryGetValue(propertyType, out var registered) ? registered : null;
            }

            var validationResults = typedValidation is not null && typedValidation.TryValidate(validators, change, out var typedResults)
                ? typedResults
                : Validate(validators, change.Property, change.GetNewValue<object?>(), change.Origin);
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

    /// <summary>
    /// Validates the new value of a change as the type it is registered for. Returns false without validating when the
    /// new value is not of that type: null for a value type, or a value of another type, which only an untyped write
    /// can store.
    /// </summary>
    private interface ITypedValidation
    {
        bool TryValidate(
            ImmutableArray<IPropertyValidator> validators,
            in SubjectPropertyChange change,
            out List<ValidationResult>? validationResults);
    }

    private sealed class TypedValidation<TProperty> : ITypedValidation
    {
        // An explicit static constructor runs before the first Register call returns, while a field initializer alone
        // only guarantees running before a field is read.
        static TypedValidation()
        {
            TypedValidations.TryAdd(typeof(TProperty), new TypedValidation<TProperty>());
        }

        /// <summary>Registers the typed commit check for <typeparamref name="TProperty"/>, once per type.</summary>
        public static void Register()
        {
            // Empty: calling it runs the static constructor, which registers the type.
        }

        public bool TryValidate(
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
    }
}
