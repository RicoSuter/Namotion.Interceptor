namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// The heat pump's current operation (input 10002).
/// </summary>
public enum LuxtronikOperatingState : ushort
{
    Heating = 0,
    HotWater = 1,
    PoolOrSolar = 2,
    UtilityLockout = 3,
    Defrost = 4,
    NoRequest = 5,

    /// <summary>Not assigned in the AIT manual; python-luxtronik reports it as heating with an external source.</summary>
    HeatingExternalSource = 6,

    Cooling = 7
}
