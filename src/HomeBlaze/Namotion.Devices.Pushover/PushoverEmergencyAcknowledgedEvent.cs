using HomeBlaze.Abstractions.Messaging;

namespace Namotion.Devices.Pushover;

/// <summary>
/// A recipient acknowledged an emergency notification of a <see cref="PushoverTarget"/>.
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
    /// The name of the device the notification was acknowledged on.
    /// </summary>
    public string? AcknowledgedByDevice { get; init; }
}
