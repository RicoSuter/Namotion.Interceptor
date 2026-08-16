using System.Collections;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

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
        if (ReferenceEquals(context.CurrentValue, context.NewValue) &&
            context.CurrentValue is IEnumerable and not string &&
            context.Property.Metadata.Type.CanContainSubjects())
        {
            var refreshHandlers = context.Property.Subject.Context.GetServices<IStructuralPropertyRefreshHandler>();
            for (var index = 0; index < refreshHandlers.Length; index++)
            {
                refreshHandlers[index].RefreshStructuralProperty(context.Property);
            }

            return;
        }

        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
        {
            next(ref context);
        }
    }
}
