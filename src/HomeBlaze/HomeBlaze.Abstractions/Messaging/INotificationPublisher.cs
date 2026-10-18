using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Messaging;

/// <summary>
/// Interface for subjects that deliver notifications to one recipient or group. A subject that only holds such
/// publishers, such as a service with several recipients, does not implement it, so that sending to all publishers
/// reaches each recipient once.
/// </summary>
[SubjectAbstraction]
[Description("Sends notification messages.")]
public interface INotificationPublisher
{
    /// <summary>
    /// Sends a notification.
    /// </summary>
    [Operation]
    Task SendNotificationAsync(Notification notification, CancellationToken cancellationToken);
}
