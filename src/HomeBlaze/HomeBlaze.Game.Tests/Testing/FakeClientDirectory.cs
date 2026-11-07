using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.Clients;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Game.Tests.Testing;

/// <summary>
/// Thread-safe, because the running game loop reads the clients while the test changes them.
/// </summary>
internal sealed class FakeClientDirectory : IGameClientDirectory
{
    private readonly Lock _lock = new();
    private readonly List<(string ClientId, Notification Notification)> _notifications = [];
    private List<GameClient>? _clients = [];

    public IReadOnlyList<(string ClientId, Notification Notification)> Notifications
    {
        get
        {
            lock (_lock)
            {
                return [.. _notifications];
            }
        }
    }

    public IReadOnlyList<GameClient>? GetClients()
    {
        lock (_lock)
        {
            return _clients?.ToList();
        }
    }

    public bool IsNamed(string id)
    {
        lock (_lock)
        {
            return _clients?.Any(client => client.Id == id && client.IsNamed) == true;
        }
    }

    public Task SendNotificationAsync(string clientId, Notification notification, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _notifications.Add((clientId, notification));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// How many times a client was renamed.
    /// </summary>
    public int RenameCount { get; private set; }

    public Task RenameAsync(string clientId, string name, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            RenameCount++;
            var clients = _clients;
            var index = clients?.FindIndex(client => client.Id == clientId) ?? -1;
            if (clients is null || index < 0)
            {
                throw new InvalidOperationException("The player has no client anymore.");
            }

            clients[index] = clients[index] with { Title = name, IsNamed = true };
        }

        return Task.CompletedTask;
    }

    public bool IsClientChange(SubjectPropertyChange change) => false;

    public void Set(string id, bool isNamed, bool isConnected, string? title = null)
    {
        lock (_lock)
        {
            _clients ??= [];
            _clients.RemoveAll(client => client.Id == id);
            _clients.Add(new GameClient(id, title ?? id, isNamed, isConnected));
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _clients?.RemoveAll(client => client.Id == id);
        }
    }

    /// <summary>
    /// Stands for a missing installation, until the next <see cref="Set"/>.
    /// </summary>
    public void RemoveInstallation()
    {
        lock (_lock)
        {
            _clients = null;
        }
    }
}
