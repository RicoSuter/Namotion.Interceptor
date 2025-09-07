namespace Namotion.Interceptor;

public static class PropertyReferenceExtensions
{
    public static PropertyReference GetPropertyReference(this IInterceptorSubject subject, string propertyName)
    {
        return new PropertyReference(subject, propertyName);
    }

    public static void SetPropertyValueWithInterception(this PropertyReference property, object? newValue, 
        Func<IInterceptorSubject, object?>? readValue, Action<IInterceptorSubject, object?> writeValue)
    {
        var executor = property.Subject.Context as IInterceptorExecutor;
        executor?.SetPropertyValue(property.Name, newValue, readValue, writeValue);
    }
}