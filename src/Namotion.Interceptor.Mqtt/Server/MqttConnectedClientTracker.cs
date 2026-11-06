using System.Collections.Concurrent;

namespace Namotion.Interceptor.Mqtt.Server;

/// <summary>
/// Tracks the IDs of the clients the broker reported connected and not yet disconnected. Keyed by client ID, so a
/// Disconnected the broker raises without a Connected (a failed CONNACK) cannot lower the count below the clients
/// that are still connected.
/// </summary>
internal sealed class MqttConnectedClientTracker
{
    private readonly ConcurrentDictionary<string, byte> _clientIds = new();

    /// <summary>
    /// Gets the number of connected client IDs.
    /// </summary>
    public int Count => _clientIds.Count;

    /// <summary>
    /// Records a client as connected; a client ID that is already connected is counted once.
    /// </summary>
    public void Add(string clientId) => _clientIds.TryAdd(clientId, 0);

    /// <summary>
    /// Records a client as disconnected; an ID that is not connected is ignored.
    /// </summary>
    public void Remove(string clientId) => _clientIds.TryRemove(clientId, out _);
}
