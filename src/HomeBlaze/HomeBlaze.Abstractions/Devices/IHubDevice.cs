using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Marker interface for devices that act as a hub/bridge for child devices.
/// </summary>
[SubjectAbstraction]
[Description("Hub device that manages child devices (bridges, controllers).")]
public interface IHubDevice
{
}
