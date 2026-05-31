using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// OPC UA fluent metadata mapper. Adds the type-self (class-level) fallback for subject-typed members on
/// top of <see cref="FluentMetadataMapper{TMetadata,TKey}"/>: a reference, collection, or dictionary
/// property merges in its element type's <c>Configure(...)</c> metadata, mirroring
/// <c>OpcUaAttributeMapper</c>'s class-level fallback.
/// </summary>
public sealed class OpcUaFluentMetadataMapper : FluentMetadataMapper<OpcUaPropertyMapping, OpcUaLookupKey>
{
    public OpcUaFluentMetadataMapper(FluentMappingRegistry<OpcUaPropertyMapping> registry)
        : base(registry)
    {
    }

    public override bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out OpcUaPropertyMapping? mapping)
    {
        Registry.TryGetTypeMetadata(property.Subject.GetType(), property.Name, out var propertyMetadata);

        OpcUaPropertyMapping? typeSelf = null;
        if (property.IsSubjectReference || property.IsSubjectCollection || property.IsSubjectDictionary)
        {
            var elementType = GetElementType(property.Type);
            Registry.TryGetTypeSelfMetadata(elementType, out typeSelf);
        }

        if (propertyMetadata is null && typeSelf is null)
        {
            mapping = null;
            return false;
        }

        mapping = propertyMetadata is null
            ? typeSelf!
            : typeSelf is null
                ? propertyMetadata
                : OpcUaPropertyMapping.Merge(propertyMetadata, typeSelf);
        return true;
    }

    private static Type GetElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType()!;

        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            return args.Length switch
            {
                2 => args[1],
                1 => args[0],
                _ => type
            };
        }

        return type;
    }
}
