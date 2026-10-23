namespace HomeBlaze.Services.Clients;

/// <summary>
/// An open circuit, one browser tab, that renders the application.
/// </summary>
public sealed class ClientConnection
{
    // One reference, so a reader never sees the id of one assignment with the name of another.
    private volatile Assignment? _assignment;
    private volatile string? _cookieClientId;

    internal ClientConnection(string connectionId, string? cookieClientId, DateTimeOffset connectedAt, IClientConnectionCallbacks callbacks)
    {
        ConnectionId = connectionId;
        _cookieClientId = cookieClientId;
        ConnectedAt = connectedAt;
        Callbacks = callbacks;
    }

    /// <summary>
    /// The random id of this circuit, which is the client id while the client is anonymous.
    /// </summary>
    public string ConnectionId { get; }

    /// <summary>
    /// The client id that the browser stores, or null when it stores none.
    /// </summary>
    public string? CookieClientId
    {
        get => _cookieClientId;
        set => _cookieClientId = value;
    }

    /// <summary>
    /// When the circuit connected.
    /// </summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>
    /// What the installation can do in the browser tab of this circuit.
    /// </summary>
    public IClientConnectionCallbacks Callbacks { get; }

    /// <summary>
    /// The id of the client this circuit belongs to, or null until the installation assigned it.
    /// </summary>
    public string? ClientId => _assignment?.ClientId;

    /// <summary>
    /// The name of the client this circuit belongs to, empty for an anonymous client, or null until assigned.
    /// </summary>
    public string? ClientName => _assignment?.ClientName;

    /// <summary>
    /// Sets the client this circuit belongs to, replacing both its id and name at once.
    /// </summary>
    public void Assign(string clientId, string clientName) => _assignment = new Assignment(clientId, clientName);

    private sealed record Assignment(string ClientId, string ClientName);
}
