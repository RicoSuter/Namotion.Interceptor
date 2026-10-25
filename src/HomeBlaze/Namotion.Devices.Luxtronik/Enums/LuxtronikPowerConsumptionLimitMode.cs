namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// Power consumption limitation mode (holding 10040).
/// </summary>
public enum LuxtronikPowerConsumptionLimitMode : ushort
{
    NoLimit = 0,
    SoftLimit = 1,
    HardLimit = 2
}
