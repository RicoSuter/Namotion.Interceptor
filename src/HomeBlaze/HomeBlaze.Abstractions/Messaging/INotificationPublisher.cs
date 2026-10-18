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
    /// Sends a notification and returns the channel's identifier of it, which the channel's acknowledgment events
    /// carry as well.
    /// </summary>
    [Operation(Description = "Sends a notification to this recipient and returns the channel's id of it. Use the Emergency priority only for safety-critical events.")]
    Task<NotificationResult> SendNotificationAsync(Notification notification, CancellationToken cancellationToken);
}
