namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Smart Grid state signalled by the utility through the EVU1 and EVU2 inputs.
/// </summary>
public enum LuxtronikSmartGridState
{
    Locked,
    Reduced,
    Normal,
    Increased
}
