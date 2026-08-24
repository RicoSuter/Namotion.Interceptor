using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking;

/// <summary>
/// Interceptor that checks if the new value is different from the current value
/// and only calls the next interceptor when the property has actually changed.
/// The singleton authority for the default equality gate on its context: a second instance would
/// be a redundant chain link, so registering one throws.
/// </summary>
[RunsFirst]
public class PropertyValueEqualityCheckHandler : IWriteInterceptor,
    ISingletonContextService<PropertyValueEqualityCheckHandler>
{
    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
        {
            next(ref context);
        }
    }
}
