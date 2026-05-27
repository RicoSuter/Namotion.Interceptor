using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Combines multiple mappers with "last wins" merge semantics.
/// </summary>
public class CompositeMapper<TMapping> : IPropertyMapper<TMapping>
    where TMapping : IPropertyMapping<TMapping>
{
    private readonly Func<TMapping, TMapping, TMapping> _merge;
    private readonly IPropertyMapper<TMapping>[] _mappers;

    public CompositeMapper(params IPropertyMapper<TMapping>[] mappers)
        : this(TMapping.Merge, mappers) { }

    public CompositeMapper(
        Func<TMapping, TMapping, TMapping> merge,
        params IPropertyMapper<TMapping>[] mappers)
    {
        _merge = merge ?? throw new ArgumentNullException(nameof(merge));
        _mappers = mappers ?? throw new ArgumentNullException(nameof(mappers));
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        mapping = default;
        var found = false;
        foreach (var inner in _mappers)
        {
            if (inner.TryGetMapping(property, rootSubject, out var partial))
            {
                mapping = found ? _merge(partial, mapping!) : partial; // Later mappers override earlier ones (partial=primary, accumulated=fallback)
                found = true;
            }
        }
        return found;
    }
}
