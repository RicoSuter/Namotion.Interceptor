using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
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
    private Dictionary<string, HomeBlazeClient>? _pendingClients;

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

        // A reload assigns outside the lock, so a reconcile on another thread may read Clients before this write
        // lands and assign its result after it; the reconcile starts from this dictionary instead, so it keeps the loaded clients.
        if (!_clientsLock.IsHeldByCurrentThread)
        {
            lock (_clientsLock)
            {
                _pendingClients = newValue;
            }
        }
    }

    /// <summary>
    /// Brings <see cref="Clients"/> in line with the open circuits: a circuit joins the named client whose token its
    /// browser stores, otherwise it is an anonymous client of its own; an unnamed client without circuit is removed.
    /// </summary>
    internal void ReconcileClients()
    {
        List<(ClientConnection Connection, string Token)>? tokensToStore = null;
        lock (_clientsLock)
        {
            var connections = _clientConnectionService.Connections;
            var current = _pendingClients ?? Clients;
            _pendingClients = null;
            var clientIds = ResolveClientIds(connections, current);
            var next = CreateClients(current, clientIds);

            // Change observers of these writes run synchronously under the lock and must not block on another thread that reconciles.
            // Replacing the dictionary attaches and detaches subjects, so it is replaced only when an entry changed.
            if (!ReferenceEquals(current, Clients) || !HasSameEntries(current, next))
            {
                Clients = next;
            }

            foreach (var (id, client) in next)
            {
                UpdateClient(id, client, connections, clientIds, ref tokensToStore);
            }

            ConnectedCircuitCount = connections.Length;
        }

        // Outside the lock: the callback runs on the dispatcher of the circuit.
        if (tokensToStore is not null)
        {
            foreach (var (connection, token) in tokensToStore)
            {
                _ = StoreClientTokenAsync(connection, token);
            }
        }
    }

    internal async Task<NotificationResult> SendNotificationAsync(HomeBlazeClient client, Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Message);

        ThrowIfNotPartOfInstallation(client);

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
            ExceptionDispatchInfo.Capture(lastException!).Throw();
        }

        return new NotificationResult { Id = Guid.NewGuid().ToString("N") };
    }

    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> unless the client is the one in <see cref="Clients"/> under its id.
    /// </summary>
    internal void ThrowIfNotPartOfInstallation(HomeBlazeClient client)
    {
        // Read from the committed Clients, so a client that a reload replaced or removed cannot act anymore.
        if (!Clients.TryGetValue(client.Id, out var current) || !ReferenceEquals(current, client))
        {
            throw new InvalidOperationException($"The client '{client.Title}' is not part of the installation.");
        }
    }

    internal async Task WriteClientConfigurationAsync(CancellationToken cancellationToken)
    {
        var configurationWriter = this.TryGetFirstParent<IConfigurationWriter>();
        if (configurationWriter is null || !await configurationWriter.WriteConfigurationAsync(this, cancellationToken))
        {
            _logger.LogWarning("The client names of the installation were not saved and will be lost on restart");
        }
    }

    private void OnClientConnectionsChanged()
    {
        // Runs inside Connect of a circuit that is initializing, which must not fail because of the installation.
        try
        {
            ReconcileClients();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Updating the clients of the installation failed");
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

    private async Task StoreClientTokenAsync(ClientConnection connection, string token)
    {
        try
        {
            await connection.Callbacks.StoreClientTokenAsync(token);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Storing the token of the client {ClientId} in its browser failed", connection.ClientId);
        }
    }

    /// <summary>
    /// Returns the client id of each connection: the named client whose token hash its browser stores, otherwise its own id.
    /// </summary>
    private static string[] ResolveClientIds(ImmutableArray<ClientConnection> connections, Dictionary<string, HomeBlazeClient> current)
    {
        Dictionary<string, string>? clientIdsByTokenHash = null;
        var clientIds = new string[connections.Length];
        for (var index = 0; index < connections.Length; index++)
        {
            var connection = connections[index];
            string? clientId = null;
            if (connection.CookieTokenHash is { } cookieTokenHash)
            {
                clientIdsByTokenHash ??= CreateClientIdsByTokenHash(current);
                clientIdsByTokenHash.TryGetValue(cookieTokenHash, out clientId);
            }

            clientIds[index] = clientId ?? connection.ConnectionId;
        }

        return clientIds;
    }

    private static Dictionary<string, string> CreateClientIdsByTokenHash(Dictionary<string, HomeBlazeClient> current)
    {
        var clientIdsByTokenHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, client) in current)
        {
            if (client.IsNamed && client.TokenHash is { } tokenHash)
            {
                clientIdsByTokenHash.TryAdd(tokenHash, id);
            }
        }

        return clientIdsByTokenHash;
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
        ref List<(ClientConnection Connection, string Token)>? tokensToStore)
    {
        var tokenHash = client.IsNamed ? client.TokenHash : null;
        var token = tokenHash is null ? null : client.GetIssuedToken(tokenHash);
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
            if (token is not null && connection.CookieTokenHash != tokenHash)
            {
                connection.CookieTokenHash = tokenHash;
                (tokensToStore ??= []).Add((connection, token));
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
