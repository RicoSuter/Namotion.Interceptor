using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Combines multiple reverse-capable mappers. Forward composition merges partial mappings with
/// "last wins" semantics; reverse lookup tries mappers in reverse order and returns the first match.
/// </summary>
public class ReverseCompositeMapper<TMapping, TKey> : IReversePropertyMapper<TMapping, TKey>
    where TMapping : IPropertyMapping<TMapping>
{
    private readonly IReversePropertyMapper<TMapping, TKey>[] _mappers;

    protected ReverseCompositeMapper(params IReversePropertyMapper<TMapping, TKey>[] mappers)
    {
        _mappers = mappers ?? throw new ArgumentNullException(nameof(mappers));

        for (var i = 0; i < _mappers.Length; i++)
        {
            if (_mappers[i] is null)
            {
                throw new ArgumentException($"Mapper at index {i} must not be null.", nameof(mappers));
            }
        }
    }

    /// <inheritdoc />
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
                // Later mappers override earlier ones (partial = primary, accumulated = fallback).
                mapping = found ? TMapping.Merge(partial, mapping!) : partial;
                found = true;
            }
        }
        return found;
    }

    /// <inheritdoc />
    public async ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        TKey key,
        RegisteredSubject subject,
        CancellationToken cancellationToken)
    {
        // Later mappers win, so reverse-iterate for early return.
        for (var i = _mappers.Length - 1; i >= 0; i--)
        {
            var found = await _mappers[i].TryGetPropertyAsync(key, subject, cancellationToken)
                .ConfigureAwait(false);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }
}
