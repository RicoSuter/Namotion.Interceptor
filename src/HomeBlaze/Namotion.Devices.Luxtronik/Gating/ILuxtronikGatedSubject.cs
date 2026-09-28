namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Firmware and feature requirements of every register of a subject, for classes reused at several addresses.
/// </summary>
internal interface ILuxtronikGatedSubject
{
    string? MinimumFirmware { get; }

    LuxtronikFeature Feature { get; }
}
