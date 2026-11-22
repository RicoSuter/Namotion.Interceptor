namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// Something that comes with a found device, such as a speaker of a household, shown under the device and not
/// selectable on its own.
/// </summary>
/// <param name="DisplayName">The name the user is shown for the item.</param>
/// <param name="Detail">A second part for the user, such as the model and host address, or null.</param>
public sealed record DiscoveredItem(string DisplayName, string? Detail);
