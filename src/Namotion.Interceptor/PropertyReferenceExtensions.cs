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
    /// </summary>
    internal static void SetPropertyValueWithInterception(this PropertyReference property, object? newValue,
        object? currentValue, Action<IInterceptorSubject, object?> writeValue, long rawTimestamp)
    {
        var executor = property.Subject.Context as InterceptorExecutor;
        executor?.SetPropertyValue(property.Name, newValue, currentValue, writeValue, rawTimestamp);
    }

    /// <summary>
    /// Publishes the calculated value of a derived property through the write chain without invoking a setter. The
    /// write has <see cref="PropertyWriteContext{TProperty}.IsDerivedRecalculation"/> set.
    /// </summary>
    /// <param name="property">The derived property.</param>
    /// <param name="newValue">The calculated value, which the getter already returns.</param>
    /// <param name="currentValue">The value published before, or null when none was.</param>
    /// <exception cref="InvalidOperationException">The property is not derived.</exception>
    public static void PublishDerivedPropertyValue(this PropertyReference property, object? newValue, object? currentValue)
    {
        if (!property.Metadata.IsDerived)
        {
            throw new InvalidOperationException($"Property '{property.Name}' is not derived.");
        }

        property.SetPropertyValueWithInterception(newValue, currentValue, static (_, _) => { }, rawTimestamp: 0);
    }
}
