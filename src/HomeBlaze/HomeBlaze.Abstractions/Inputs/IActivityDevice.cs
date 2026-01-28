using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Interface for devices with selectable activities.
/// </summary>
public interface IActivityDevice
{
    /// <summary>
    /// The currently active activity.
    /// </summary>
    [State]
    DeviceActivity? CurrentActivity { get; }

    /// <summary>
    /// The available activities on this device.
    /// </summary>
    [State]
    DeviceActivity[] Activities { get; }

    /// <summary>
    /// Sets the active activity by its ID.
    /// </summary>
    [Operation]
    Task SetActivityAsync(string activityId, CancellationToken cancellationToken);
}
