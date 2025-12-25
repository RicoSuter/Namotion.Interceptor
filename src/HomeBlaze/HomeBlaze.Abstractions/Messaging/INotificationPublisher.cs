using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Messaging;

/// <summary>
/// Interface for subjects that can send notifications.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Sends a notification message.
    /// </summary>
    [Operation]
    Task SendNotificationAsync(string message, CancellationToken cancellationToken);
}
