using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Maps C# properties to OPC UA node configuration.
/// Used by both client and server.
/// </summary>
public interface IOpcUaNodeMapper
{
    /// <summary>
    /// Gets OPC UA configuration for a property, or null if not mapped.
    /// Returns partial configuration; use CompositeNodeMapper to merge multiple mappers.
    /// </summary>
    /// <param name="property">The registered property to get configuration for.</param>
    /// <returns>The configuration, or null if this mapper doesn't handle the property.</returns>
    OpcUaNodeConfiguration? TryGetNodeConfiguration(RegisteredSubjectProperty property);

    /// <summary>
    /// Client only: Finds property matching an OPC UA node (reverse lookup for discovery).
    /// Server implementations should return null.
    /// </summary>
    /// <param name="subject">The subject to search in.</param>
    /// <param name="nodeReference">The OPC UA node reference to match.</param>
    /// <param name="session">The OPC UA session for namespace resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching property, or null if not found.</returns>
    Task<RegisteredSubjectProperty?> TryGetPropertyAsync(
        RegisteredSubject subject,
        ReferenceDescription nodeReference,
        ISession session,
        CancellationToken cancellationToken);
}
