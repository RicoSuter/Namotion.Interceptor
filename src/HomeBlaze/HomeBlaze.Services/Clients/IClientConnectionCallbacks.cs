using HomeBlaze.Abstractions.Messaging;

namespace HomeBlaze.Services.Clients;

/// <summary>
/// What the installation can do in the browser tab of a <see cref="ClientConnection"/>.
/// </summary>
public interface IClientConnectionCallbacks
{
    /// <summary>
    /// Shows the notification in the tab and completes once it is shown, not when it is closed.
    /// </summary>
    Task ShowNotificationAsync(Notification notification, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the secret token of the named client in the browser, so later tabs join that client.
    /// </summary>
    Task StoreClientTokenAsync(string token);
}
