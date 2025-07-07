namespace Namotion.Interceptor;

public static class PropertyReferenceExtensions
{
    public static PropertyReference GetPropertyReference(this IInterceptorSubject subject, string propertyName)
    {
        return new PropertyReference(subject, propertyName);
    }
    
    public static void SetPropertyMetadata(this IInterceptorSubject subject, PropertyReference property, SubjectPropertyMetadata propertyMetadata)
    {
        subject.Data[property.Name + ":Metadata"] = propertyMetadata;
    }

    public static bool TryGetPropertyMetadata(this IInterceptorSubject subject, PropertyReference property, out SubjectPropertyMetadata? propertyMetadata)
    {
        propertyMetadata = 
            subject.TryGetData(property.Name + ":Metadata", out var value) && 
            value is SubjectPropertyMetadata resultPropertyMetadata ? resultPropertyMetadata : null;

        return propertyMetadata is not null;
    }
}