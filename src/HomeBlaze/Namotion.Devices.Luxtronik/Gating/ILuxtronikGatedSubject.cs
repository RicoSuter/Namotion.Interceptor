namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Firmware and feature requirements of every register of a subject, for classes reused at several addresses.
/// </summary>
internal interface ILuxtronikGatedSubject
{
    Version? MinimumFirmwareVersion { get; }

    LuxtronikFeature Feature { get; }
}
