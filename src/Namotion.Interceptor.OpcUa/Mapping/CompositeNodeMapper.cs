using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Combines multiple node mappers with merge semantics.
/// Later mappers in the list take priority for conflicting values ("last wins").
/// </summary>
public class CompositeNodeMapper : IOpcUaNodeMapper
{
    private readonly IOpcUaNodeMapper[] _mappers;

    /// <summary>
    /// Creates a composite mapper from multiple mappers.
    /// </summary>
    /// <param name="mappers">Mappers in order (later mappers override earlier ones).</param>
    public CompositeNodeMapper(params IOpcUaNodeMapper[] mappers)
    {
        _mappers = mappers;
    }

    /// <inheritdoc />
    public OpcUaNodeConfiguration? TryGetNodeConfiguration(RegisteredSubjectProperty property)
    {
        OpcUaNodeConfiguration? result = null;

        foreach (var mapper in _mappers)
        {
            var config = mapper.TryGetNodeConfiguration(property);
            if (config is not null)
            {
                // Later mappers override earlier ones ("last wins")
                result = config.MergeWith(result);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken)
    {
        foreach (var mapper in _mappers)
        {
            var property = await mapper.TryGetPropertyAsync(
                subject, nodeReference, session, cancellationToken);
            if (property is not null)
            {
                return property;
            }
        }

        return null;
    }
}
