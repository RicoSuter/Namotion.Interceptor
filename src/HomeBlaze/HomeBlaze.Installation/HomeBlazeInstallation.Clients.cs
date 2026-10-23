using System.Collections.Immutable;
using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services.Clients;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Installation;

public partial class HomeBlazeInstallation
{
    private readonly Lock _clientsLock = new();

    /// <summary>
    /// The connected and the named clients by id. Only named clients persist.
    /// </summary>
    [Configuration]
    public partial Dictionary<string, HomeBlazeClient> Clients { get; set; }

    /// <summary>
    /// The open circuits, so every open tab counts once.
    /// </summary>
    [State(Position = 440)]
    [Category("Runtime")]
    public partial int ConnectedCircuitCount { get; internal set; }

    // Set before Clients changes, so a client reachable through Clients always knows its installation and id.
    partial void OnClientsChanging(ref Dictionary<string, HomeBlazeClient> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<string, HomeBlazeClient>();
        foreach (var (id, client) in newValue)
        {
            client.Installation = this;
            client.Id = id;
        }
    }

    /// <summary>
    /// Brings <see cref="Clients"/> in line with the open circuits: a circuit joins the named client its browser stores
    /// the id of, otherwise it is an anonymous client of its own; an unnamed client without circuit is removed.
    /// </summary>
    internal void ReconcileClients()
    {
        List<(ClientConnection Connection, string ClientId)>? idsToStore = null;
        lock (_clientsLock)
        {
            var connections = _clientConnectionService.Connections;
            var current = Clients;
            var clientIds = ResolveClientIds(connections, current);
            var next = CreateClients(current, clientIds);

            // Replacing the dictionary attaches and detaches subjects, so it is replaced only when an entry changed.
            if (!HasSameEntries(current, next))
            {
                Clients = next;
            }

            foreach (var (id, client) in Clients)
            {
                UpdateClient(id, client, connections, clientIds, ref idsToStore);
            }

            ConnectedCircuitCount = connections.Length;
        }

        // Outside the lock: the callback runs on the dispatcher of the circuit.
        if (idsToStore is not null)
        {
            foreach (var (connection, clientId) in idsToStore)
            {
                _ = StoreClientIdAsync(connection, clientId);
            }
        }
    }

    internal async Task<NotificationResult> SendNotificationAsync(HomeBlazeClient client, Notification notification, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Message);

        // Read from the committed Clients, so a client that a reload replaced or removed cannot send anymore.
        if (!Clients.TryGetValue(client.Id, out var current) || !ReferenceEquals(current, client))
        {
            throw new InvalidOperationException($"The client '{client.Title}' is not part of the installation.");
        }

        var connections = _clientConnectionService.Connections.Where(connection => connection.ClientId == client.Id).ToArray();
        if (connections.Length == 0)
        {
            throw new InvalidOperationException($"The client '{client.Title}' is not connected.");
        }

        var shownCount = 0;
        Exception? lastException = null;
        await Task.WhenAll(connections.Select(async connection =>
        {
            try
            {
                await connection.Callbacks.ShowNotificationAsync(notification, cancellationToken);
                Interlocked.Increment(ref shownCount);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref lastException, exception);
                _logger.LogWarning(exception, "Showing a notification in a tab of the client {Client} failed", client.Title);
            }
        }));

        if (shownCount == 0)
        {
            throw lastException!;
        }

        return new NotificationResult { Id = Guid.NewGuid().ToString("N") };
    }

    internal async Task WriteClientConfigurationAsync(CancellationToken cancellationToken)
    {
        var configurationWriter = this.TryGetFirstParent<IConfigurationWriter>();
        if (configurationWriter is null || !await configurationWriter.WriteConfigurationAsync(this, cancellationToken))
        {
            _logger.LogWarning("The client names of the installation were not saved and will be lost on restart");
        }
    }

    private Task RenameConnectionAsync(ClientConnection connection, string name, CancellationToken cancellationToken)
    {
        var clientId = connection.ClientId;
        if (clientId is null || !Clients.TryGetValue(clientId, out var client))
        {
            throw new InvalidOperationException("This browser is not known to the installation yet.");
        }

        return client.RenameAsync(name, cancellationToken);
    }

    private async Task StoreClientIdAsync(ClientConnection connection, string clientId)
    {
        try
        {
            await connection.Callbacks.StoreClientIdAsync(clientId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Storing the client id {ClientId} in its browser failed", clientId);
        }
    }

    /// <summary>
    /// Returns the client id of each connection: the named client its browser stores the id of, otherwise its own id.
    /// </summary>
    private static string[] ResolveClientIds(ImmutableArray<ClientConnection> connections, Dictionary<string, HomeBlazeClient> current)
    {
        var clientIds = new string[connections.Length];
        for (var index = 0; index < connections.Length; index++)
        {
            var connection = connections[index];
            clientIds[index] = connection.CookieClientId is { } cookieClientId &&
                current.TryGetValue(cookieClientId, out var stored) && stored.IsNamed
                    ? cookieClientId
                    : connection.ConnectionId;
        }

        return clientIds;
    }

    /// <summary>
    /// Keeps the named clients and the clients with a connection, and adds a client for each new client id.
    /// </summary>
    private static Dictionary<string, HomeBlazeClient> CreateClients(Dictionary<string, HomeBlazeClient> current, string[] clientIds)
    {
        var next = new Dictionary<string, HomeBlazeClient>(current.Count + clientIds.Length);
        foreach (var (id, client) in current)
        {
            if (client.IsNamed || Array.IndexOf(clientIds, id) >= 0)
            {
                next[id] = client;
            }
        }

        foreach (var id in clientIds)
        {
            if (!next.ContainsKey(id))
            {
                next[id] = new HomeBlazeClient();
            }
        }

        return next;
    }

    private static void UpdateClient(
        string id,
        HomeBlazeClient client,
        ImmutableArray<ClientConnection> connections,
        string[] clientIds,
        ref List<(ClientConnection Connection, string ClientId)>? idsToStore)
    {
        var connectionCount = 0;
        var lastConnected = client.LastConnected;
        for (var index = 0; index < connections.Length; index++)
        {
            if (clientIds[index] != id)
            {
                continue;
            }

            var connection = connections[index];
            connectionCount++;
            if (lastConnected is null || connection.ConnectedAt > lastConnected)
            {
                lastConnected = connection.ConnectedAt;
            }

            connection.Assign(id, client.Name);
            if (client.IsNamed && connection.CookieClientId != id)
            {
                connection.CookieClientId = id;
                (idsToStore ??= []).Add((connection, id));
            }
        }

        client.ConnectionCount = connectionCount;
        client.IsConnected = connectionCount > 0;
        client.LastConnected = lastConnected;
    }

    private static bool HasSameEntries(Dictionary<string, HomeBlazeClient> current, Dictionary<string, HomeBlazeClient> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        foreach (var (id, client) in next)
        {
            if (!current.TryGetValue(id, out var existing) || !ReferenceEquals(existing, client))
            {
                return false;
            }
        }

        return true;
    }
}
