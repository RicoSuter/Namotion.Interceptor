using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Uses reflection and optional <see cref="IServiceProvider"/> to create subjects and subject collections.
/// </summary>
public class DefaultSubjectFactory : ISubjectFactory
{
    public static DefaultSubjectFactory Instance { get; } = new();

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
        var collectionType = typeof(List<>).MakeGenericType(itemType);

        var collection = (IList)Activator.CreateInstance(collectionType)!;
        foreach (var subject in children)
        {
            collection.Add(subject);
        }

        return (IEnumerable<IInterceptorSubject?>)collection;
    }

    /// <summary>
    /// Creates a dictionary of subjects from a list of key-value pairs.
    /// </summary>
    /// <param name="propertyType">The dictionary type (e.g., Dictionary&lt;string, TSubject&gt;).</param>
    /// <param name="children">The key-subject pairs to add to the dictionary.</param>
    /// <returns>The created dictionary.</returns>
    public object CreateSubjectDictionary(Type propertyType, IEnumerable<(string key, IInterceptorSubject subject)> children)
    {
        // Find the dictionary interface to get key and value types
        var dictionaryInterface = propertyType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (dictionaryInterface is null)
        {
            throw new InvalidOperationException($"Type '{propertyType.Name}' is not a dictionary type.");
        }

        var keyType = dictionaryInterface.GenericTypeArguments[0];
        var valueType = dictionaryInterface.GenericTypeArguments[1];
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);

        var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;
        foreach (var (key, subject) in children)
        {
            dictionary.Add(key, subject);
        }

        return dictionary;
    }
}
