using System.Diagnostics.CodeAnalysis;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Source of truth for code-based (fluent) mapping. Holds per-property segment plus metadata keyed by
/// (declaring type, member) and class-level (type) metadata keyed by type. Resolution walks the runtime type,
/// its base classes, then its interfaces, most-derived first, so a base or interface registration applies to
/// derived and implementing types.
/// </summary>
public sealed class FluentMappingRegistry<TMetadata>
    where TMetadata : class
{
    private readonly Dictionary<(Type Type, string Member), Entry> _propertyMetadata = new();
    private readonly Dictionary<Type, TMetadata> _typeMetadata = new();

    /// <summary>Registers a per-property mapping (segment plus metadata). A null segment means "use the BrowseName".</summary>
    public void AddPropertyMetadata(Type declaringType, string member, string? segment, TMetadata metadata)
        => _propertyMetadata[(declaringType, member)] = new Entry(segment, metadata);

    /// <summary>Registers class-level (type) metadata for a type.</summary>
    public void AddTypeMetadata(Type type, TMetadata metadata)
        => _typeMetadata[type] = metadata;

    /// <summary>
    /// Returns true when a per-property registration exists for the given holder type and member, walking the
    /// type hierarchy. <paramref name="segment"/> is the registered segment override, or null to mean "use the
    /// member's BrowseName".
    /// </summary>
    public bool TryGetSegment(Type subjectType, string member, out string? segment)
    {
        if (TryResolveProperty(subjectType, member, out var entry))
        {
            segment = entry.Segment;
            return true;
        }

        segment = null;
        return false;
    }

    /// <summary>Resolves the per-property metadata for a member, walking the type hierarchy.</summary>
    public bool TryGetPropertyMetadata(Type subjectType, string member, [NotNullWhen(true)] out TMetadata? metadata)
    {
        if (TryResolveProperty(subjectType, member, out var entry))
        {
            metadata = entry.Metadata;
            return true;
        }

        metadata = null;
        return false;
    }

    /// <summary>Resolves class-level (type) metadata for a type, walking the type hierarchy.</summary>
    public bool TryGetTypeMetadata(Type type, [NotNullWhen(true)] out TMetadata? metadata)
    {
        foreach (var candidate in WalkTypeHierarchy(type))
        {
            if (_typeMetadata.TryGetValue(candidate, out metadata))
                return true;
        }

        metadata = null;
        return false;
    }

    private bool TryResolveProperty(Type subjectType, string member, out Entry entry)
    {
        foreach (var candidate in WalkTypeHierarchy(subjectType))
        {
            if (_propertyMetadata.TryGetValue((candidate, member), out entry))
                return true;
        }

        entry = default;
        return false;
    }

    // Most-derived first: the runtime type, then each base class up the chain, then interfaces.
    private static IEnumerable<Type> WalkTypeHierarchy(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;

        foreach (var interfaceType in type.GetInterfaces())
            yield return interfaceType;
    }

    private readonly record struct Entry(string? Segment, TMetadata Metadata);
}
