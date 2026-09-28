namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Overall heating control (holding 10065). Value 1 is not defined.
/// </summary>
public enum LuxtronikOverallHeatingMode : ushort
{
    Individual = 0,
    Offset = 2,
    Level = 3
}
