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
        var itemType = index is null ? propertyType
            : propertyType.IsArray ? propertyType.GetElementType()
            : GetContainerTypes(propertyType, dictionary: propertyType.IsSubjectDictionaryType()).Element;

        return subjectFactory.CreateSubject(
            itemType ?? throw new InvalidOperationException("Unknown collection element type"),
            serviceProvider);
    }

    internal static Type GetCollectionElementType(Type propertyType)
        => GetContainerTypes(propertyType, dictionary: false).Element;

    internal static (Type Key, Type Value) GetDictionaryKeyAndValueTypes(Type propertyType)
    {
        // The dictionary shape returns a key or throws, so the shared lookup's nullable key is never null here.
        var (key, value) = GetContainerTypes(propertyType, dictionary: true);
        return (key!, value);
    }

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

            // The classifier that routed the property here accepts a container when any one of its
            // enumerable instantiations has a subject-capable item type, so a container that also
            // enumerates something else must not be rejected as ambiguous. Only a container with more
            // than one subject-capable item type still is.
            if (itemTypes.Length > 1)
            {
                var subjectItemTypes = itemTypes.Where(static types => types.Element.IsSubjectReferenceType()).ToArray();
                if (subjectItemTypes.Length == 1)
                {
                    return subjectItemTypes[0];
                }
            }

            // The two fallbacks below read the declaring type's own generic arguments by position, which
            // is the only place a non-generic IDictionary or ICollection names its item types. A
            // declaration that orders them differently resolves backwards and cannot be detected here.
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
