namespace Namotion.Devices.Wallbox.Model;

public enum WallboxChargerStatus
{
    Unknown = 0,
    Disconnected,
    Error,
    Ready,
    Waiting,
    Locked,
    Updating,
    Scheduled,
    Paused,
    WaitingForCarDemand,
    WaitingInQueueByPowerSharing,
    WaitingInQueueByPowerBoost,
    WaitingMidFailed,
    WaitingMidSafetyMarginExceeded,
    WaitingInQueueByEcoSmart,
    Charging,
    Discharging,
    LockedCarConnected
}
