using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Interface for devices with executable actions.
/// </summary>
public interface IActionDevice
{
    /// <summary>
    /// The available actions on this device.
    /// </summary>
    [State]
    DeviceAction[] Actions { get; }

    /// <summary>
    /// Executes an action by its ID.
    /// </summary>
    [Operation]
    Task ExecuteActionAsync(string actionId, CancellationToken cancellationToken);
}
