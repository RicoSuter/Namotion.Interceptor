using System.Collections.Concurrent;
using System.Net;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// Tracks the connections the broker reported connected and not yet disconnected, each identified by its client ID and
/// remote endpoint. A Disconnected the broker raises without a Connected (a failed CONNACK) cannot lower the count
/// below the connections that are still open, and a late Disconnected of an old connection cannot remove a newer
/// connection of the same client ID.
/// </summary>
internal sealed class MqttConnectedClientTracker
{
    private readonly ConcurrentDictionary<(string ClientId, EndPoint? RemoteEndPoint), byte> _connections = new();

    /// <summary>
    /// Gets the number of open connections.
    /// </summary>
    public int Count => _connections.Count;

    /// <summary>
    /// Records a connection as open; a connection that is already open is counted once.
    /// </summary>
    public void Add(string clientId, EndPoint? remoteEndPoint) => _connections.TryAdd((clientId, remoteEndPoint), 0);

    /// <summary>
    /// Records a connection as closed; a connection that is not open is ignored.
    /// </summary>
    public void Remove(string clientId, EndPoint? remoteEndPoint) => _connections.TryRemove((clientId, remoteEndPoint), out _);
}
