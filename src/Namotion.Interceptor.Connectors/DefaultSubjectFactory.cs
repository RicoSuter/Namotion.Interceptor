using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Connectors.Updates.Internal;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Uses reflection and optional <see cref="IServiceProvider"/> to create subjects and subject collections.
/// </summary>
public class DefaultSubjectFactory : ISubjectFactory
{
    public static DefaultSubjectFactory Instance { get; } = new();

    private static readonly ConcurrentDictionary<Type, Type> ListTypeCache = new();
    private static readonly ConcurrentDictionary<Type, Type> DictionaryTypeCache = new();
    private static readonly ConcurrentDictionary<Type, Func<IList, object>> CollectionMaterializerCache = new();

    /// <inheritdoc />
    public virtual IInterceptorSubject CreateSubject(Type itemType, IServiceProvider? serviceProvider)
    {
        var item = (serviceProvider is not null
               ? ActivatorUtilities.CreateInstance(serviceProvider, itemType) as IInterceptorSubject
               : Activator.CreateInstance(itemType) as IInterceptorSubject)
           ?? throw new InvalidOperationException("Could not create subject.");

        return item;
    }

    /// <inheritdoc />
    public IEnumerable<IInterceptorSubject?> CreateSubjectCollection(Type propertyType, params IEnumerable<IInterceptorSubject?> children)
    {
        if (propertyType.IsArray)
        {
            var childSubjectList = new List<IInterceptorSubject?>(children);
            var elementType = propertyType.GetElementType() ?? throw new InvalidOperationException("Unknown array element type.");
            var array = Array.CreateInstance(elementType, childSubjectList.Count);
            for (var arrayIndex = 0; arrayIndex < childSubjectList.Count; arrayIndex++)
            {
                array.SetValue(childSubjectList[arrayIndex], arrayIndex);
            }

            return (IInterceptorSubject?[])array;
        }

        var itemType = propertyType.GenericTypeArguments[0];
        var collectionType = ListTypeCache.GetOrAdd(itemType, static t => typeof(List<>).MakeGenericType(t));

        var collection = (IList)Activator.CreateInstance(collectionType)!;
        foreach (var subject in children)
        {
            collection.Add(subject);
        }

        // A List<T> satisfies the usual declared types (List<T>, IList<T>, IReadOnlyList<T>, ...).
        // Read-only and immutable declared types such as ImmutableArray<T> are not assignable from
        // it, so they are materialized from the working list instead of failing on assignment.
        var materialize = CollectionMaterializerCache.GetOrAdd(propertyType, CreateCollectionMaterializer);
        return (IEnumerable<IInterceptorSubject?>)materialize(collection);
    }

    private static Func<IList, object> CreateCollectionMaterializer(Type propertyType)
    {
        var itemType = propertyType.GenericTypeArguments[0];
        var listType = typeof(List<>).MakeGenericType(itemType);
        if (propertyType.IsAssignableFrom(listType))
        {
            return static list => list;
        }

        var enumerableType = typeof(IEnumerable<>).MakeGenericType(itemType);

        // Immutable collections expose a static empty instance and a range append returning
        // their own type (ImmutableArray<T>, ImmutableList<T>, ImmutableHashSet<T>, ...).
        var empty = TryGetStaticEmptyInstance(propertyType);
        if (empty is not null)
        {
            var addRange = TryFindSingleParameterMethod(propertyType, "AddRange", enumerableType);
            if (addRange is not null && propertyType.IsAssignableFrom(addRange.ReturnType))
            {
                return list => addRange.Invoke(empty, [list])!;
            }
        }

        // Read-only wrappers are constructed from the working list (e.g. ReadOnlyCollection<T>).
        var constructor = propertyType.GetConstructor([enumerableType])
                          ?? propertyType.GetConstructor([typeof(IList<>).MakeGenericType(itemType)]);
        if (constructor is not null)
        {
            return list => constructor.Invoke([list]);
        }

        throw new InvalidOperationException(
            $"Could not create a subject collection of type '{propertyType}': the type is not assignable " +
            $"from 'List<{itemType.Name}>' and provides no way to build it from a sequence of items.");
    }

    private static object? TryGetStaticEmptyInstance(Type type)
    {
        var field = type.GetField("Empty", BindingFlags.Public | BindingFlags.Static);
        if (field is not null && type.IsAssignableFrom(field.FieldType))
        {
            return field.GetValue(null);
        }

        var property = type.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static);
        if (property is not null && type.IsAssignableFrom(property.PropertyType))
        {
            return property.GetValue(null);
        }

        return null;
    }

    private static MethodInfo? TryFindSingleParameterMethod(Type type, string name, Type parameterType)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != name || method.IsGenericMethodDefinition)
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == parameterType)
            {
                return method;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IDictionary CreateSubjectDictionary(Type propertyType, IDictionary<object, IInterceptorSubject> entries)
    {
        var dictionaryType = DictionaryTypeCache.GetOrAdd(propertyType, static t =>
        {
            var keyType = t.GenericTypeArguments[0];
            var valueType = t.GenericTypeArguments[1];
            return typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        });

        var keyType = propertyType.GenericTypeArguments[0];
        var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;
        foreach (var entry in entries)
        {
            var key = DictionaryKeyConverter.Convert(entry.Key, keyType);
            dictionary.Add(key, entry.Value);
        }

        return dictionary;
    }
}
