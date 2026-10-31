using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.Clients;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Game.Tests.Testing;

internal sealed class FakeClientDirectory : IGameClientDirectory
{
    private readonly Lock _lock = new();
    private readonly List<(string ClientId, Notification Notification)> _notifications = [];

    /// <summary>
    /// The clients; null stands for a missing installation.
    /// </summary>
    public List<GameClient>? Clients { get; set; } = [];

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

    public IReadOnlyList<GameClient>? GetClients() => Clients?.ToList();

    public Task SendNotificationAsync(string clientId, Notification notification, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _notifications.Add((clientId, notification));
        }

        return Task.CompletedTask;
    }

    public bool IsClientChange(SubjectPropertyChange change) => false;

    public void Set(string id, bool isNamed, bool isConnected, string? title = null)
    {
        Clients ??= [];
        Clients.RemoveAll(client => client.Id == id);
        Clients.Add(new GameClient(id, title ?? id, isNamed, isConnected));
    }

    public void Remove(string id) => Clients?.RemoveAll(client => client.Id == id);
}
