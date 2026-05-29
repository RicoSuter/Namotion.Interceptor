using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Connectors.Mapping;

/// <summary>
/// Combines multiple reverse-capable mappers. Forward composition merges partial mappings field by field
/// with "last wins" semantics (a later mapper's non-null field overrides an earlier one). Reverse lookup
/// tries the mappers in reverse order and returns the first match; it resolves per mapper rather than
/// inverting the merged mapping, so mappers in one composite must not override each other's reverse key
/// field (the usual composition pairs a topic/name mapper with a metadata-only one, which never conflict).
/// </summary>
public class ReverseCompositeMapper<TMapping, TKey> : IReversePropertyMapper<TMapping, TKey>
    where TMapping : IPropertyMapping<TMapping>
{
    private readonly IReversePropertyMapper<TMapping, TKey>[] _mappers;

    protected ReverseCompositeMapper(params IReversePropertyMapper<TMapping, TKey>[] mappers)
    {
        ArgumentNullException.ThrowIfNull(mappers);

        // Copy so a caller's explicit array cannot be mutated after construction (the params path is already fresh).
        _mappers = (IReversePropertyMapper<TMapping, TKey>[])mappers.Clone();

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
