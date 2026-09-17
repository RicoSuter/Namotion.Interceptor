using System.Collections;
using System.Collections.Concurrent;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors;

public static class SubjectFactoryExtensions
{
    private static readonly ConcurrentDictionary<(Type Type, bool Dictionary), (Type? Key, Type Element)> ContainerTypes = new();

    public static IInterceptorSubject CreateSubject(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property)
    {
        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        return subjectFactory.CreateSubject(property.Type, serviceProvider);
    }

    public static IInterceptorSubject CreateCollectionSubject(this ISubjectFactory subjectFactory, RegisteredSubjectProperty property, object? index)
    {
        var serviceProvider = property.Parent.Subject.Context.TryGetService<IServiceProvider>();
        return CreateCollectionSubject(subjectFactory, property.Type, index, serviceProvider);
    }

    /// <summary>
    /// Creates a collection/dictionary item subject from a property type and optional index/key.
    /// Uses the property type to derive the element type (array element, dict value, or list element).
    /// </summary>
    internal static IInterceptorSubject CreateCollectionSubject(this ISubjectFactory subjectFactory, Type propertyType, object? index, IServiceProvider? serviceProvider)
    {
        Type? itemType;
        if (index is null)
        {
            itemType = propertyType;
        }
        else if (propertyType.IsArray)
        {
            itemType = propertyType.GetElementType();
        }
        else
        {
            itemType = propertyType.IsSubjectDictionaryType()
                ? GetDictionaryKeyAndValueTypes(propertyType).Value
                : GetCollectionElementType(propertyType);
        }

        return subjectFactory.CreateSubject(
            itemType ?? throw new InvalidOperationException("Unknown collection element type"),
            serviceProvider);
    }

    internal static Type GetCollectionElementType(Type propertyType)
        => GetContainerTypes(propertyType, dictionary: false).Element;

    internal static (Type Key, Type Value) GetDictionaryKeyAndValueTypes(Type propertyType)
    {
        // The dictionary shape either yields a key type or throws, which the collection shape does not,
        // so this is the only place the nullable key of the shared lookup is resolved.
        var (key, value) = GetContainerTypes(propertyType, dictionary: true);
        return (key!, value);
    }

    /// <remarks>
    /// Which shape to read is the caller's requirement rather than a fact about the type, so it stays a
    /// parameter: reading a dictionary-declared property as a positional collection, or the reverse, is
    /// how a declaration that cannot carry the incoming update is turned into a <see cref="NotSupportedException"/>.
    /// </remarks>
    private static (Type? Key, Type Element) GetContainerTypes(Type propertyType, bool dictionary)
    {
        return ContainerTypes.GetOrAdd((propertyType, dictionary), static shape =>
        {
            var itemTypes = shape.Type.GetInterfaces().Append(shape.Type)
                .Where(type => type.IsGenericType && (shape.Dictionary
                    ? type.GetGenericTypeDefinition() == typeof(IDictionary<,>) || type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                    : type.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                .Select(type => (Key: shape.Dictionary ? type.GenericTypeArguments[0] : null,
                    Element: type.GenericTypeArguments[shape.Dictionary ? 1 : 0]))
                .Distinct()
                .ToArray();

            // Legacy IDictionary wrappers used their two generic arguments as key/value types.
            if (shape.Dictionary && itemTypes.Length == 0 && typeof(IDictionary).IsAssignableFrom(shape.Type) &&
                shape.Type.GenericTypeArguments is { Length: 2 } genericArguments)
            {
                return (genericArguments[0], genericArguments[1]);
            }

            if (!shape.Dictionary && itemTypes.Length == 0 && typeof(ICollection).IsAssignableFrom(shape.Type) &&
                shape.Type.GenericTypeArguments is { Length: 1 } collectionArguments)
            {
                return (null, collectionArguments[0]);
            }

            return itemTypes.Length == 1
                ? itemTypes[0]
                : throw new NotSupportedException($"Cannot infer a unique collection element type from '{shape.Type}'. Declare a collection with a known element type.");
        });
    }
}
