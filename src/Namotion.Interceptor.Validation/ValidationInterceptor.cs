using System.ComponentModel.DataAnnotations;
using System.Text;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Interceptor that validates property values using registered validators before writing.
/// Runs only for locally originated writes: values a source sent (<see cref="ChangeOriginKind.FromSource"/>)
/// or confirmed (<see cref="ChangeOriginKind.Confirmed"/>) are applied unvalidated, because the external
/// system already holds them and rejecting them would diverge the model rather than repair anything.
/// A local commit replay is still validated, since a rejection there is cleanly recoverable, which is
/// why this runs before the transaction interceptor: a local write is validated at capture and again
/// when the commit replays it.
/// </summary>
[RunsBefore(typeof(SubjectTransactionInterceptor))]
public class ValidationInterceptor : IWriteInterceptor
{
    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Only local writes are validated. A non-local origin means an external system already holds
        // this value, so rejecting it cannot cleanly undo anything: the commit would revert an
        // accepted source write (a visible flap), or an inbound apply would simply leave the model
        // diverged from its source. Placed before validator resolution so the inbound apply path
        // does no validation work at all.
        // The EFFECTIVE origin, not context.Origin: a hook that transformed a stamped value produces
        // a locally computed value that publishes as Local and flows outbound, so it must be validated.
        if (context.GetEffectiveOrigin().Kind != ChangeOriginKind.Local)
        {
            next(ref context);
            return;
        }

        var validators = context.Property.Subject.Context.GetServices<IPropertyValidator>();

        // The effective origin, which the gate above has already established is Local. Passing
        // context.Origin instead would hand a validator FromSource for a hook-transformed value that
        // this interceptor just decided to validate, and a validator that opts out of non-local
        // writes would then skip the very write the gate exists to catch.
        var validationContext = new PropertyValidationContext<TProperty>(
            context.Property, context.NewValue, ChangeOrigin.Local);

        List<ValidationResult>? additionalErrors = null;
        foreach (var validator in validators)
        {
            foreach (var error in validator.Validate(in validationContext))
            {
                additionalErrors ??= [];
                additionalErrors.Add(error);
            }
        }

        if (additionalErrors is not null)
        {
            var sb = new StringBuilder();
            foreach (var error in additionalErrors)
            {
                sb.Append('\n');
                sb.Append(error.ErrorMessage);
            }
            
            throw new ValidationException(sb.ToString());
        }

        next(ref context);
    }
}
