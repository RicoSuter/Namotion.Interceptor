using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Dynamic;

public class DynamicSubject : IInterceptorSubject
{
    private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties
        = ReadOnlyDictionary<string, SubjectPropertyMetadata>.Empty;

    private IInterceptorExecutor? _context;

    [JsonIgnore] IInterceptorSubjectContext IInterceptorSubject.Context => _context ??= new InterceptorExecutor(this);

    [JsonIgnore] ConcurrentDictionary<(string? property, string key), object?> IInterceptorSubject.Data { get; } = new();

    [JsonIgnore] IReadOnlyDictionary<string, SubjectPropertyMetadata> IInterceptorSubject.Properties => _properties;

    public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
    {
        _properties = _properties
            .Concat(properties.Select(p => new KeyValuePair<string, SubjectPropertyMetadata>(p.Name, p)))
            .ToFrozenDictionary();
    }
}