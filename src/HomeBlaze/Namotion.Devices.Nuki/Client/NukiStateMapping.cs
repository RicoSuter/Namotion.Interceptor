using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;

namespace Namotion.Devices.Nuki.Client;

/// <summary>
/// Maps the state numbers of the bridge API to states. A number without a state, such as uncalibrated, gives null.
/// </summary>
internal static class NukiStateMapping
{
    public static DoorLockState? ToDoorLockState(int? state) => state switch
    {
        1 => DoorLockState.Locked,
        2 => DoorLockState.Unlocking,
        3 or 6 => DoorLockState.Unlocked, // 6 is unlocked by lock 'n' go
        4 => DoorLockState.Locking,
        5 => DoorLockState.Unlatched,
        7 => DoorLockState.Unlatching,
        254 => DoorLockState.Jammed, // motor blocked
        _ => null
    };

    public static DoorState? ToDoorState(int? doorSensorState) => doorSensorState switch
    {
        2 => DoorState.Closed,
        3 => DoorState.Open,
        _ => null
    };

    public static NukiOpenerState? ToOpenerState(int? state) => state switch
    {
        0 => NukiOpenerState.Untrained,
        1 => NukiOpenerState.Online,
        3 => NukiOpenerState.RingToOpenActive,
        5 => NukiOpenerState.Open,
        7 => NukiOpenerState.Opening,
        253 => NukiOpenerState.BootRun,
        _ => null
    };
}
