using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Installation;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Game.Clients;

/// <summary>
/// Reads the clients of the installation subject of the object graph.
/// </summary>
internal sealed class InstallationClientDirectory(Func<IInterceptorSubjectContext?> getContext) : IGameClientDirectory
{
    private HomeBlazeInstallation? _installation;

    public IReadOnlyList<GameClient>? GetClients()
    {
        var installation = FindInstallation();
        if (installation is null)
        {
            return null;
        }

        var clients = installation.Clients;
        var result = new List<GameClient>(clients.Count);
        foreach (var (id, client) in clients)
        {
            result.Add(new GameClient(id, client.Title ?? id, client.IsNamed, client.IsConnected));
        }

        return result;
    }

    public Task SendNotificationAsync(string clientId, Notification notification, CancellationToken cancellationToken)
    {
        var installation = FindInstallation() ?? throw new InvalidOperationException("The game needs the installation to send messages.");
        if (!installation.Clients.TryGetValue(clientId, out var client))
        {
            throw new InvalidOperationException("The player has no client anymore.");
        }

        return client.SendNotificationAsync(notification, cancellationToken);
    }

    public bool IsClientChange(SubjectPropertyChange change) =>
        change.Property.Subject is HomeBlazeClient ||
        change.Property.Subject is HomeBlazeInstallation && change.Property.Name == nameof(HomeBlazeInstallation.Clients);

    private HomeBlazeInstallation? FindInstallation()
    {
        var registry = getContext()?.TryGetService<ISubjectRegistry>();
        if (registry is null)
        {
            return null;
        }

        // The installation is a single instance, so the one found stays valid while the registry knows it.
        var installation = _installation;
        if (installation is null || !registry.KnownSubjects.ContainsKey(installation))
        {
            installation = registry.KnownSubjects.Keys.OfType<HomeBlazeInstallation>().FirstOrDefault();
            _installation = installation;
        }

        return installation;
    }
}
