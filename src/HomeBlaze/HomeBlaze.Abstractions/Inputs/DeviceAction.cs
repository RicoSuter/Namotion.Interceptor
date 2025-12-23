namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Represents an action that can be executed on a device.
/// </summary>
/// <param name="Id">The unique identifier of the action.</param>
/// <param name="Title">The display title of the action.</param>
public readonly record struct DeviceAction(string Id, string Title);
