namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// Running compressors and auxiliary heaters (input 10000).
/// </summary>
[Flags]
public enum LuxtronikHeatPumpStatus : ushort
{
    None = 0,
    Compressor1 = 1,
    Compressor2 = 2,
    AuxiliaryHeater1 = 4,
    AuxiliaryHeater2 = 8,
    AuxiliaryHeater3 = 16
}
