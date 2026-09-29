namespace Namotion.Devices.Luxtronik.Enums;

/// <summary>
/// State of an operating mode (inputs 10003 to 10007): <see cref="Off"/> means the mode is switched off (Aus),
/// <see cref="NoRequest"/> switched on without demand, <see cref="Requested"/> demand waiting to run, and
/// <see cref="Running"/> running.
/// </summary>
public enum LuxtronikModeStatus : ushort
{
    Off = 0,
    NoRequest = 1,
    Requested = 2,
    Running = 3
}
