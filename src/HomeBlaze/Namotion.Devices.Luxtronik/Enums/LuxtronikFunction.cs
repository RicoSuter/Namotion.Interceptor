namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// Controller functions that can be configured; the value is the offset of its discrete input from 10000.
/// </summary>
public enum LuxtronikFunction
{
    None = -1,
    Heating = 0,
    HotWater = 1,
    Cooling = 2,
    Pool = 3,
    Solar = 4,
    RoomControlUnit = 5,
    MixingCircuit1Heating = 6,
    MixingCircuit1Cooling = 7,
    MixingCircuit2Heating = 8,
    MixingCircuit2Cooling = 9,
    MixingCircuit3Heating = 10,
    MixingCircuit3Cooling = 11
}
