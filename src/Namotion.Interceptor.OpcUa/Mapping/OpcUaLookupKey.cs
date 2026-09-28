using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Mapping;

/// <summary>
/// Key for reverse-looking up a property from an OPC UA node reference.
/// </summary>
/// <param name="Reference">The browsed node reference to resolve.</param>
/// <param name="Session">The OPC UA session for namespace resolution.</param>
/// <param name="RootSubject">
/// The source's connected root subject. Path-based mappers resolve property paths relative to this
/// root so that reverse lookup matches the forward mapping even when the connected root is itself
/// nested inside a larger object graph.
/// </param>
public readonly record struct OpcUaLookupKey(
    ReferenceDescription Reference,
    ISession Session,
    IInterceptorSubject RootSubject);
