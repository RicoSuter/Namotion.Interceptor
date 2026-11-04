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
    /// Publishes a derived property's calculated value through the write chain, with
    /// <see cref="PropertyWriteContext{TProperty}.IsDerivedRecalculation"/> set. A nonzero
    /// <paramref name="rawTimestamp"/> seeds the new <see cref="PropertyWriteContext{TProperty}"/>'s timestamp
    /// cache directly, which spares a recalculation cascade the
    /// <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/> scope push it would otherwise
    /// need to share the trigger's captured time; 0 resolves it lazily. With
    /// <paramref name="finalValueIsNewValue"/> set, publishing reuses <paramref name="newValue"/> instead of invoking
    /// the getter again (see <see cref="PropertyWriteContext{TProperty}.FinalValueIsNewValue"/>).
    /// </summary>
    internal static void SetPropertyValueWithInterception(this PropertyReference property, object? newValue,
        object? currentValue, Action<IInterceptorSubject, object?> writeValue, long rawTimestamp, bool finalValueIsNewValue)
    {
        var executor = property.Subject.Context as InterceptorExecutor;
        executor?.SetPropertyValue(property.Name, newValue, currentValue, writeValue, rawTimestamp, finalValueIsNewValue);
    }

    /// <summary>
    /// Publishes the initial value of a just-added derived property through the write chain, as a change from
    /// null. The value is read through the property's getter and no setter is invoked. The write has
    /// <see cref="PropertyWriteContext{TProperty}.IsDerivedRecalculation"/> set, and the published value is read
    /// again when the change is published. When the getter returns null and <c>PropertyValueEqualityCheckHandler</c>
    /// is registered, nothing is published, because that handler drops a write whose new value equals its current
    /// value.
    /// </summary>
    /// <param name="property">The derived property.</param>
    /// <exception cref="InvalidOperationException">The property is not derived.</exception>
    public static void PublishInitialDerivedPropertyValue(this PropertyReference property)
    {
        var metadata = property.Metadata;
        if (!metadata.IsDerived)
        {
            throw new InvalidOperationException($"Property '{property.Name}' is not derived.");
        }

        var value = metadata.GetValue?.Invoke(property.Subject);
        property.SetPropertyValueWithInterception(value, null, static (_, _) => { }, rawTimestamp: 0, finalValueIsNewValue: false);
    }
}
