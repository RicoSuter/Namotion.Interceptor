using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Interceptor that checks if the new value is different from the current value
/// and only calls the next interceptor when the property has actually changed.
/// </summary>
[RunsFirst]
public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Only stamped (inbound) writes record an outcome for correction detection; a local write
        // records nothing and pays a single predictable branch.
        var stamped = context.Origin.Kind != ChangeOriginKind.Local;

        if (EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
        {
            // Suppressed: the projected value already equals the stored value. A stamped suppressed
            // write is the correction candidate; valueUnchanged is this typed comparison itself.
            if (stamped)
            {
                PendingOrigin.SetOutcome(valueUnchanged: true);
            }

            return;
        }

        next(ref context);

        if (stamped)
        {
            PendingOrigin.SetOutcome(valueUnchanged: false);
        }
    }
}
