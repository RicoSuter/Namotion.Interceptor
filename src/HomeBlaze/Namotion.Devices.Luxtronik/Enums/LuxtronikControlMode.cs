namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// How a Smart Home Interface control influences a circuit.
/// </summary>
public enum LuxtronikControlMode : ushort
{
    NoInfluence = 0,
    Setpoint = 1,
    Offset = 2,
    Level = 3
}
