using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Interface for devices with selectable activities.
/// </summary>
[SubjectAbstraction]
[Description("Device with selectable activities (e.g., TV input, scene).")]
public interface IActivityDevice
{
    /// <summary>
    /// The currently active activity.
    /// </summary>
    [State(Position = 160)]
    DeviceActivity? CurrentActivity { get; }

    /// <summary>
    /// The available activities on this device.
    /// </summary>
    [State(Position = 161)]
    DeviceActivity[] Activities { get; }

    /// <summary>
    /// Sets the active activity by its ID.
    /// </summary>
    [Operation]
    Task SetActivityAsync(string activityId, CancellationToken cancellationToken);
}
