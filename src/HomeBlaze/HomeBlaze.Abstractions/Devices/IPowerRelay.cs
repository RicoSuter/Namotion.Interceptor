using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Marker interface for power relays (switches that control electrical power).
/// </summary>
[SubjectAbstraction]
[Description("Power relay that controls electrical power via switching.")]
public interface IPowerRelay : ISwitchDevice
{
}
