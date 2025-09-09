namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceDataExtensions
{
    public static T GetOrAddPropertyData<T>(this PropertyReference property, string key, Func<T> valueFactory)
    {
        return (T)property.Subject.Data.GetOrAdd($"{property.Name}:{key}", static (_, f) => f(), valueFactory)!;
    }

    public static T AddOrUpdatePropertyData<T, TArg>(this PropertyReference property, string key, Action<T, TArg> updateAction, TArg arg)
        where T : new()
    {
        return (T)property.Subject.Data.AddOrUpdate($"{property.Name}:{key}", static (_, tuple) =>
        {
            var value = new T();
            tuple.updateAction(value, tuple.arg);
            return value;
        }, static (_, value, tuple) =>
        {
            tuple.updateAction((T)value!, tuple.arg);
            return value;
        }, (updateAction, arg))!;
    }
}