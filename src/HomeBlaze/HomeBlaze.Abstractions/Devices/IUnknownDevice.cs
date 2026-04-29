using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Marker interface for devices without a recognized specific type.
/// </summary>
[SubjectAbstraction]
[Description("Device without a recognized specific type.")]
public interface IUnknownDevice
{
}
