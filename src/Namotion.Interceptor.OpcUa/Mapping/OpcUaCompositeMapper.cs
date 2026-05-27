using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Connectors.Mapping;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Composite that adds OPC UA-specific reverse lookup (later mappers win, reverse-iterate for
/// early return) on top of <see cref="CompositeMapper{TMapping}"/>'s forward composition.
/// </summary>
public sealed class OpcUaCompositeMapper
    : IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>
{
    private readonly CompositeMapper<OpcUaPropertyMapping> _forward;
    private readonly IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>[] _mappers;

    public OpcUaCompositeMapper(
        params IReversePropertyMapper<OpcUaPropertyMapping, OpcUaLookupKey>[] mappers)
    {
        _mappers = mappers;
        _forward = new CompositeMapper<OpcUaPropertyMapping>(
            mappers.Cast<IPropertyMapper<OpcUaPropertyMapping>>().ToArray());
    }

    public bool TryGetMapping(
        RegisteredSubjectProperty property,
        IInterceptorSubject rootSubject,
        [NotNullWhen(true)] out OpcUaPropertyMapping? mapping)
        => _forward.TryGetMapping(property, rootSubject, out mapping);

    public async ValueTask<RegisteredSubjectProperty?> TryGetPropertyAsync(
        OpcUaLookupKey key, RegisteredSubject rootSubject, CancellationToken cancellationToken)
    {
        for (var i = _mappers.Length - 1; i >= 0; i--)
        {
            var found = await _mappers[i].TryGetPropertyAsync(key, rootSubject, cancellationToken)
                .ConfigureAwait(false);
            if (found is not null) return found;
        }
        return null;
    }
}
