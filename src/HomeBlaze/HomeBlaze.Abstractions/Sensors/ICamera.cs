namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Combined interface for cameras.
/// Composes ICameraState (readable) and ICameraController (commandable).
/// </summary>
public interface ICamera : ICameraState, ICameraController
{
}
