using HomeBlaze.Abstractions.Messaging;

namespace Namotion.Devices.Pushover;

/// <summary>
/// A recipient acknowledged an emergency notification of a <see cref="PushoverTarget"/>. Emitted once per emergency.
/// </summary>
public sealed record PushoverEmergencyAcknowledgedEvent : IEvent
{
    /// <summary>
    /// When the recipient acknowledged the notification.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The target the notification was sent to.
    /// </summary>
    public required PushoverTarget Target { get; init; }

    /// <summary>
    /// The acknowledged emergency.
    /// </summary>
    public required PushoverEmergency Emergency { get; init; }

    /// <summary>
    /// The receipt of the emergency, the same as the <see cref="NotificationResult.Id"/> its send returned.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The name of the device the notification was acknowledged on.
    /// </summary>
    public string? AcknowledgedByDevice { get; init; }
}
