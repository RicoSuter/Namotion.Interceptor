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
        var path = GetPathFromExpression(selector.Body);
        _mappings[path] = mapping;
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        [NotNullWhen(true)] out TMapping? mapping)
    {
        var path = GetPathFromProperty(property);
        if (_mappings.TryGetValue(path, out var stored) && stored is not null)
        {
            mapping = stored;
            return true;
        }
        mapping = default;
        return false;
    }

    private static string GetPathFromExpression(Expression expression)
    {
        var parts = new List<string>();
        var current = expression;
        while (current is MemberExpression member)
        {
            parts.Insert(0, member.Member.Name);
            current = member.Expression;
        }
        return string.Join(".", parts);
    }

    private static string GetPathFromProperty(RegisteredSubjectProperty property)
    {
        var parts = new List<string> { property.Name };
        var currentSubject = property.Parent;

        while (currentSubject.Parents.Length > 0)
        {
            var parent = currentSubject.Parents[0];
            parts.Insert(0, parent.Property.Name);
            currentSubject = parent.Property.Parent;
        }

        return string.Join(".", parts);
    }
}
