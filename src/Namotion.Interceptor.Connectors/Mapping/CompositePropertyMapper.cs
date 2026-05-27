using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

public class CompositePropertyMapper<TMapping> : IPropertyMapper<TMapping>
    where TMapping : IPropertyMapping<TMapping>
{
    private readonly Func<TMapping, TMapping, TMapping> _merge;
    private readonly IPropertyMapper<TMapping>[] _mappers;

    public CompositePropertyMapper(params IPropertyMapper<TMapping>[] mappers)
        : this(TMapping.Merge, mappers) { }

    public CompositePropertyMapper(
        Func<TMapping, TMapping, TMapping> merge,
        params IPropertyMapper<TMapping>[] mappers)
    {
        _merge = merge ?? throw new ArgumentNullException(nameof(merge));
        _mappers = mappers ?? throw new ArgumentNullException(nameof(mappers));
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        mapping = default;
        var found = false;
        foreach (var inner in _mappers)
        {
            if (inner.TryGetMapping(property, out var partial))
            {
                mapping = found ? _merge(partial, mapping!) : partial;
                found = true;
            }
        }
        return found;
    }
}
