namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Provides diagnostic information about the OPC UA client connection state.
/// Thread-safe for reading current values.
/// </summary>
public class OpcUaClientDiagnostics
{
    private readonly OpcUaSubjectClientSource _source;

    internal OpcUaClientDiagnostics(OpcUaSubjectClientSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Gets a value indicating whether the client is currently connected to the server.
    /// </summary>
    public bool IsConnected => _source.SessionManager?.IsConnected ?? false;

    /// <summary>
    /// Gets a value indicating whether the client is currently attempting to reconnect.
    /// </summary>
    public bool IsReconnecting => _source.SessionManager?.IsReconnecting ?? false;

    /// <summary>
    /// Gets the current session identifier, or null if not connected.
    /// </summary>
    public string? SessionId => _source.SessionManager?.CurrentSession?.SessionId?.ToString();

    /// <summary>
    /// Gets the number of active OPC UA subscriptions.
    /// </summary>
    public int SubscriptionCount => _source.SessionManager?.Subscriptions.Count ?? 0;

    /// <summary>
    /// Gets the number of monitored items across all subscriptions.
    /// </summary>
    public int MonitoredItemCount => _source.SessionManager?.SubscriptionManager.MonitoredItems.Count ?? 0;
}
