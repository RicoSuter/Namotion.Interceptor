using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

public abstract class FluentPropertyMapperBase<TSubject, TMapping> : IPropertyMapper<TMapping>
{
    private readonly ConcurrentDictionary<string, TMapping> _mappings = new();

    protected void SetMapping<TValue>(Expression<Func<TSubject, TValue>> selector, TMapping mapping)
    {
        var path = PropertyPathHelper.GetPathFromExpression(selector.Body);
        _mappings[path] = mapping;
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        var path = PropertyPathHelper.GetPathFromProperty(property);
        if (_mappings.TryGetValue(path, out var stored) && stored is not null)
        {
            mapping = stored;
            return true;
        }
        mapping = default;
        return false;
    }
}
