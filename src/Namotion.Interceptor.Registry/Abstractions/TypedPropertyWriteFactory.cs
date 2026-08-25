using System.Collections.Concurrent;
using System.Reflection;

namespace Namotion.Interceptor.Registry.Abstractions;

/// <summary>
/// Builds the boxed metadata setter of a dynamically registered property. The value arrives
/// boxed, so routing the write on its compile-time type would classify every dynamic property
/// as structural and put scalar writes through the lifecycle gate on every update. Instead the
/// setter is built once per property, via one cached typed factory per declared type, to call
/// the public typed write entry with the declared type as the generic argument, so the write
/// routes and runs exactly like a generated property of that type. Duplicated in
/// Namotion.Interceptor.Dynamic on purpose: the two assemblies share no reference besides the
/// core, and these few lines are cheaper than new public surface there.
/// </summary>
internal static class TypedPropertyWriteFactory
{
    private delegate Action<IInterceptorSubject, object?> SetterFactory(
        string propertyName,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?> setValue);

    private static readonly MethodInfo CreateTypedSetterMethod = typeof(TypedPropertyWriteFactory)
        .GetMethod(nameof(CreateTypedSetter), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, SetterFactory> Factories = new();

    public static Action<IInterceptorSubject, object?> CreateSetter(
        Type declaredType,
        string propertyName,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?> setValue)
    {
        var factory = Factories.GetOrAdd(
            declaredType,
            static type => (SetterFactory)CreateTypedSetterMethod
                .MakeGenericMethod(type)
                .CreateDelegate(typeof(SetterFactory)));

        return factory(propertyName, getValue, setValue);
    }

    private static Action<IInterceptorSubject, object?> CreateTypedSetter<TProperty>(
        string propertyName,
        Func<IInterceptorSubject, object?>? getValue,
        Action<IInterceptorSubject, object?> setValue)
    {
        // One wrapper per property, not per write: the typed chain terminal invokes it and pays
        // the single box back into the stored object-typed setter.
        Action<IInterceptorSubject, TProperty> writeValue = (subject, value) => setValue(subject, value);

        return (subject, newValue) =>
        {
            var currentValue = getValue?.Invoke(subject);
            if ((newValue is TProperty || (newValue is null && default(TProperty) is null)) &&
                (currentValue is TProperty || (currentValue is null && default(TProperty) is null)))
            {
                subject.Executor.SetPropertyValue(propertyName, (TProperty)newValue!, (TProperty)currentValue!, writeValue);
            }
            else
            {
                // A null (a first write into a null backing store, say) or a box of another type
                // is not representable in a non-nullable typed chain. Such a write keeps the
                // pre-typed behavior: the object-typed chain carries both values untouched to the
                // stored setter, at the price of the structural route that an object-typed write
                // takes. Only writes both of whose values fit the declared type run typed above,
                // so a value-typed property pays this at most until its backing store holds a
                // value of the declared type.
                subject.Executor.SetPropertyValue<object?>(propertyName, newValue, currentValue, setValue);
            }
        };
    }
}
