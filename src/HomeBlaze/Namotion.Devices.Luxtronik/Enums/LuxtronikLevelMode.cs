namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// Smart Home Interface level for heating and hot water.
/// </summary>
public enum LuxtronikLevelMode : ushort
{
    NoInfluence = 0,
    RaisedWithTimeProgram = 1,
    RaisedIgnoringTimeProgram = 2,
    Lowered = 3
}
