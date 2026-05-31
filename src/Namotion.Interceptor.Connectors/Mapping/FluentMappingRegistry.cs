using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Source of truth for code-based (fluent) mapping. Holds type-level segment plus metadata keyed by
/// (declaring type, member) and type-self metadata keyed by type. Resolution walks the runtime type, its
/// base classes, then its interfaces, most-derived first, so a base or interface registration applies to
/// derived and implementing types.
/// </summary>
public sealed class FluentMappingRegistry<TMetadata> : IFluentSegmentSource
    where TMetadata : class
{
    private readonly Dictionary<(Type Type, string Member), Entry> _typeLevel = new();
    private readonly Dictionary<Type, TMetadata> _typeSelf = new();

    /// <summary>Registers a type-level mapping for a member. A null segment means "use the BrowseName".</summary>
    public void AddType(Type declaringType, string member, string? segment, TMetadata metadata)
        => _typeLevel[(declaringType, member)] = new Entry(segment, metadata);

    /// <summary>Registers type-self (class-level) metadata for a type.</summary>
    public void AddTypeSelf(Type type, TMetadata metadata)
        => _typeSelf[type] = metadata;

    /// <inheritdoc />
    public bool TryGetSegment(Type subjectType, string member, out string? segment)
    {
        if (TryResolveType(subjectType, member, out var entry))
        {
            segment = entry.Segment;
            return true;
        }

        segment = null;
        return false;
    }

    /// <summary>Resolves the type-level metadata for a member, walking the type hierarchy.</summary>
    public bool TryGetTypeMetadata(Type subjectType, string member, [NotNullWhen(true)] out TMetadata? metadata)
    {
        if (TryResolveType(subjectType, member, out var entry))
        {
            metadata = entry.Metadata;
            return true;
        }

        metadata = null;
        return false;
    }

    /// <summary>Resolves type-self metadata for a type, walking the type hierarchy.</summary>
    public bool TryGetTypeSelfMetadata(Type type, [NotNullWhen(true)] out TMetadata? metadata)
    {
        foreach (var candidate in WalkTypeHierarchy(type))
        {
            if (_typeSelf.TryGetValue(candidate, out metadata))
                return true;
        }

        metadata = null;
        return false;
    }

    private bool TryResolveType(Type subjectType, string member, out Entry entry)
    {
        foreach (var candidate in WalkTypeHierarchy(subjectType))
        {
            if (_typeLevel.TryGetValue((candidate, member), out entry))
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
