using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Combined interface for cameras.
/// Composes ICameraState (readable) and ICameraController (commandable).
/// </summary>
[SubjectAbstraction]
[Description("Combined camera with state and capture operations.")]
public interface ICamera : ICameraState, ICameraController
{
}
