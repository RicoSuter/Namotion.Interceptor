using Namotion.Devices.Luxtronik.Enums;

namespace Namotion.Devices.Luxtronik.Gating;

/// <summary>
/// Firmware and feature requirements of every register of a subject, for classes reused at several addresses.
/// </summary>
internal interface ILuxtronikGatedSubject
{
    Version? MinimumFirmwareVersion { get; }

    LuxtronikFeature Feature { get; }
}
