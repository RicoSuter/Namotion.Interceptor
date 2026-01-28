using HomeBlaze.Abstractions.Messaging;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Base record for device events with common properties.
/// </summary>
public abstract record DeviceEvent : IEvent
{
    /// <summary>
    /// The timestamp when the event occurred.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The identifier of the device that raised the event.
    /// </summary>
    public required string DeviceId { get; init; }
}
