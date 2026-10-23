using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services.Clients;

namespace HomeBlaze.Installation.Tests.Testing;

public sealed class FakeClientCallbacks : IClientConnectionCallbacks
{
    private readonly Lock _lock = new();
    private readonly List<Notification> _notifications = [];
    private readonly List<string> _storedClientIds = [];

    public bool FailsToShow { get; init; }

    public IReadOnlyList<Notification> Notifications
    {
        get
        {
            lock (_lock)
            {
                return [.. _notifications];
            }
        }
    }

    public IReadOnlyList<string> StoredClientIds
    {
        get
        {
            lock (_lock)
            {
                return [.. _storedClientIds];
            }
        }
    }

    public Task ShowNotificationAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (FailsToShow)
        {
            throw new InvalidOperationException("The tab is gone.");
        }

        lock (_lock)
        {
            _notifications.Add(notification);
        }

        return Task.CompletedTask;
    }

    public Task StoreClientIdAsync(string clientId)
    {
        lock (_lock)
        {
            _storedClientIds.Add(clientId);
        }

        return Task.CompletedTask;
    }
}
