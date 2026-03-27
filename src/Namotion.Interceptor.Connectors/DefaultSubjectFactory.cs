using System.Collections;
using System.Collections.Concurrent;
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

        return (IEnumerable<IInterceptorSubject?>)collection;
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
