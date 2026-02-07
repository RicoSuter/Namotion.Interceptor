using System.Collections;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.Connectors;

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

    /// <inheritdoc />
    public IDictionary CreateSubjectDictionary(Type propertyType, IDictionary<object, IInterceptorSubject> entries)
    {
        // Get key and value types from the dictionary generic type arguments
        var keyType = propertyType.GenericTypeArguments[0];
        var valueType = propertyType.GenericTypeArguments[1];
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);

        var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;
        foreach (var entry in entries)
        {
            // Convert key to the target key type if needed
            var key = keyType.IsEnum
                ? Enum.Parse(keyType, Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!)
                : Convert.ChangeType(entry.Key, keyType, CultureInfo.InvariantCulture);
            dictionary.Add(key, entry.Value);
        }

        return dictionary;
    }
}
