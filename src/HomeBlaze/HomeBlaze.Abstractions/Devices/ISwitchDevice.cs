namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Combined interface for devices that can be switched on/off.
/// Composes ISwitchState (readable) and ISwitchController (commandable).
/// </summary>
public interface ISwitchDevice : ISwitchState, ISwitchController
{
}
