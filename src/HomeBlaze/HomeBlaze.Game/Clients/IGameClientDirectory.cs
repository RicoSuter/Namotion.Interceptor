using HomeBlaze.Abstractions.Messaging;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Game.Clients;

/// <summary>
/// The clients the players of a game belong to.
/// </summary>
internal interface IGameClientDirectory
{
    /// <summary>
    /// Gets the clients, or null while there is no installation, which keeps every player.
    /// </summary>
    IReadOnlyList<GameClient>? GetClients();

    /// <summary>
    /// Shows the notification in the open tabs of the client.
    /// </summary>
    /// <exception cref="InvalidOperationException">The client is unknown or not connected.</exception>
    Task SendNotificationAsync(string clientId, Notification notification, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the change may change what <see cref="GetClients"/> returns.
    /// </summary>
    bool IsClientChange(SubjectPropertyChange change);
}
