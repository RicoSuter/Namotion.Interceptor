namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Connector where the local subject IS the authoritative SERVER.
/// The local object is the source of truth. Serves data to external clients.
/// Examples: OPC UA server, GraphQL server, MQTT publisher, WebSocket server.
/// </summary>
public interface ISubjectServerConnector : ISubjectConnector
{
}
