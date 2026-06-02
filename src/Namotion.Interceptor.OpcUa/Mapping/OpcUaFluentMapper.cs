using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Code-based OPC UA mapper produced by <see cref="OpcUaFluentMapperBuilder{TRoot}.Build"/>. Resolves browse
/// names via a <see cref="FluentPathProvider{TMetadata}"/> (inherited from <see cref="OpcUaPathProviderMapper"/>)
/// and layers the registered node metadata plus the class-level (type-self) fallback for subject-typed members
/// on top, mirroring <c>OpcUaAttributeMapper</c>'s class-level fallback. Compose it into an
/// <see cref="OpcUaCompositeMapper"/>, typically after the attribute mappers so fluent wins on conflicts.
/// Reverse lookup is inherited from the base mapper.
/// </summary>
public sealed class OpcUaFluentMapper : OpcUaPathProviderMapper
{
    private readonly FluentMappingRegistry<OpcUaPropertyMapping> _registry;

    public OpcUaFluentMapper(FluentMappingRegistry<OpcUaPropertyMapping> registry, char pathSeparator = '.')
        : base(new FluentPathProvider<OpcUaPropertyMapping>(registry, pathSeparator))
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public override bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out OpcUaPropertyMapping? mapping)
    {
        if (!base.TryGetMapping(property, rootSubject, out var pathMapping))
        {
            mapping = null;
            return false;
        }

        _registry.TryGetTypeMetadata(property.Subject.GetType(), property.Name, out var propertyMetadata);

        OpcUaPropertyMapping? typeSelf = null;
        if (property.IsSubjectReference || property.IsSubjectCollection || property.IsSubjectDictionary)
        {
            var elementType = GetElementType(property.Type);
            _registry.TryGetTypeSelfMetadata(elementType, out typeSelf);
        }

        // Property metadata wins over the element type-self; both win over the base path mapping (browse name).
        var metadata = propertyMetadata;
        if (typeSelf is not null)
            metadata = metadata is null ? typeSelf : OpcUaPropertyMapping.Merge(metadata, typeSelf);

        mapping = metadata is null ? pathMapping : OpcUaPropertyMapping.Merge(metadata, pathMapping);
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
