namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Power consumption limitation mode (holding 10040).
/// </summary>
public enum LuxtronikPowerLimitMode : ushort
{
    NoLimit = 0,
    SoftLimit = 1,
    HardLimit = 2
}
