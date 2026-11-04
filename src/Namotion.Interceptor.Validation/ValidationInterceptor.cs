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
/// <see cref="TransactionCheckMode"/>, and the commit checks also validate the derived properties that depend on
/// the changed properties; a write recognized by <see cref="SubjectTransaction.TryConsumeCommitWrite"/> and the derived
/// properties it recalculates are not validated again.
/// </summary>
[RunsAfter(typeof(SubjectTransactionInterceptor))]
[RunsBefore(typeof(DerivedPropertyChangeHandler), typeof(PropertyChangeInterceptor), typeof(LifecycleInterceptor))]
public sealed class ValidationInterceptor : IWriteInterceptor, ITransactionHandler
{
    private static readonly ConcurrentDictionary<Type, ITypedValidation> TypedValidations = new();

    // Set while a write recognized by TryConsumeCommitWrite runs the rest of the chain.
    [ThreadStatic]
    private static bool _isApplyingCheckedWrite;

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
        if (SubjectTransaction.TryConsumeCommitWrite(this, context.Property, context.NewValue))
        {
            // Derived recalculations run inside next and see the values applied so far, while the commit check
            // validated them over the final set, so they are not validated again.
            var wasApplyingCheckedWrite = _isApplyingCheckedWrite;
            _isApplyingCheckedWrite = true;
            try
            {
                next(ref context);
            }
            finally
            {
                _isApplyingCheckedWrite = wasApplyingCheckedWrite;
            }

            return;
        }

        if (!_isApplyingCheckedWrite || !context.Property.Metadata.IsDerived)
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

        ValidateDerivedProperties(context.Changes, ref failedChanges, ref errors);

        if (failedChanges is not null)
        {
            throw new SubjectTransactionValidationException(failedChanges, errors!);
        }
    }

    /// <summary>
    /// Validates the derived properties that depend on the changed properties, computed over the transaction's values
    /// (reads in the checks return them). An invalid one is reported as a change whose old and new value are its
    /// computed value. Their recalculations during the apply are not validated again, see
    /// <see cref="WriteProperty{TProperty}"/>.
    /// </summary>
    private static void ValidateDerivedProperties(
        ReadOnlySpan<SubjectPropertyChange> changes,
        ref List<SubjectPropertyChange>? failedChanges,
        ref List<Exception>? errors)
    {
        HashSet<PropertyReference>? visited = null;
        foreach (ref readonly var change in changes)
        {
            // Dependencies are flattened, so this includes derived properties of derived properties.
            foreach (var derivedProperty in change.Property.GetUsedByProperties().Items)
            {
                // A single change's used-by set has no duplicates, so only several changes need the set.
                if (changes.Length > 1 && !(visited ??= new HashSet<PropertyReference>(PropertyReference.Comparer)).Add(derivedProperty))
                {
                    continue;
                }

                var validators = derivedProperty.Subject.Context.GetServices<IPropertyValidator>();
                if (validators.IsDefaultOrEmpty)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
                }
                catch (Exception)
                {
                    // A recalculation keeps the last value when the getter throws, so there is no new value to validate.
                    continue;
                }

                var validationResults = Validate(validators, derivedProperty, value, ChangeOrigin.Local);
                if (validationResults is not null)
                {
                    (failedChanges ??= []).Add(SubjectPropertyChange.Create(
                        derivedProperty, ChangeOrigin.Local, change.ChangedTimestamp, null, value, value));
                    (errors ??= []).Add(new PropertyValidationException(derivedProperty, value, validationResults));
                }
            }
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
