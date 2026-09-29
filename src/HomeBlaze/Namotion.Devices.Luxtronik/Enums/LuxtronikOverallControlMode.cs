namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// Overall heating control (holding 10065). Value 1 is not defined.
/// </summary>
public enum LuxtronikOverallControlMode : ushort
{
    Individual = 0,
    Offset = 2,
    Level = 3
}
