namespace Namotion.Devices.Nuki;

/// <summary>
/// The type of a Nuki device, with the numbers of the bridge API.
/// </summary>
public enum NukiDeviceType
{
    /// <summary>Nuki Smart Lock 1.0 or 2.0.</summary>
    SmartLock = 0,

    /// <summary>Nuki Opener.</summary>
    Opener = 2,

    /// <summary>Nuki Smart Door.</summary>
    SmartDoor = 3,

    /// <summary>Nuki Smart Lock 3.0 (Pro) and later.</summary>
    SmartLock3 = 4
}
