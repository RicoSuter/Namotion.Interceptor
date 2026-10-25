namespace Namotion.Devices.Nuki.Client;

/// <summary>
/// The lock actions of a smart lock in the bridge API.
/// </summary>
internal enum NukiLockAction
{
    Unlock = 1,
    Lock = 2,
    Unlatch = 3,
    LockNGo = 4,
    LockNGoWithUnlatch = 5
}

/// <summary>
/// The lock actions of an Opener in the bridge API.
/// </summary>
internal enum NukiOpenerAction
{
    ActivateRingToOpen = 1,
    DeactivateRingToOpen = 2,
    ElectricStrikeActuation = 3,
    ActivateContinuousMode = 4,
    DeactivateContinuousMode = 5
}
