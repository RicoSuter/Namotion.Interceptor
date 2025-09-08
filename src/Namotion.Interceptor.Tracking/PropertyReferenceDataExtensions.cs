namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceDataExtensions
{
    public static T GetOrAddPropertyData<T>(this PropertyReference property, string key, Func<T> valueFactory)
    {
        return (T)property.Subject.Data.GetOrAdd($"{property.Name}:{key}", static (_, f) => f(), valueFactory)!;
    }
}