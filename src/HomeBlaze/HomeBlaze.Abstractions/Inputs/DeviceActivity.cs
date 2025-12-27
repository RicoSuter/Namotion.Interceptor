namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Represents an activity that can be selected on a device.
/// </summary>
/// <param name="Id">The unique identifier of the activity.</param>
/// <param name="Title">The display title of the activity.</param>
public readonly record struct DeviceActivity(string Id, string Title);
