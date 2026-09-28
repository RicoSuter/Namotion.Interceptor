namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// State of an operating mode (inputs 10003 to 10007).
/// </summary>
public enum LuxtronikModeStatus : ushort
{
    Disabled = 0,
    NoRequest = 1,
    Requested = 2,
    Running = 3
}
