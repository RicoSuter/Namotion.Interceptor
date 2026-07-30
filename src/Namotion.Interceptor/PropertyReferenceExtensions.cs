using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

public static class PropertyReferenceExtensions
{
    public static PropertyReference GetPropertyReference(this IInterceptorSubject subject, string propertyName)
    {
        return new PropertyReference(subject, propertyName);
    }

    public static void SetPropertyValueWithInterception(this PropertyReference property, object? newValue,
        object? currentValue, Action<IInterceptorSubject, object?> writeValue)
    {
        var executor = property.Subject.Context as IInterceptorExecutor;
        executor?.SetPropertyValue(property.Name, newValue, currentValue, writeValue);
    }

    /// <summary>
    /// Cascade re-entry path: invokes the write chain with a pre-resolved raw timestamp so the
    /// new <see cref="PropertyWriteContext{TProperty}"/>'s cache is seeded directly. Bypasses
    /// the <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/> scope-push
    /// dance the derived-cascade would otherwise need to share the trigger's captured time.
    /// Pass <c>finalValueIsNewValue</c> as true when the new value is already the stabilized getter
    /// output, so publishing reuses it instead of invoking the getter again (see
    /// <see cref="PropertyWriteContext{TProperty}.FinalValueIsNewValue"/>).
    /// </summary>
    internal static void SetPropertyValueWithInterception(this PropertyReference property, object? newValue,
        object? currentValue, Action<IInterceptorSubject, object?> writeValue, long rawTimestamp, bool finalValueIsNewValue)
    {
        var executor = property.Subject.Context as InterceptorExecutor;
        executor?.SetPropertyValue(property.Name, newValue, currentValue, writeValue, rawTimestamp, finalValueIsNewValue);
    }
}
