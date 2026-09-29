using Namotion.Devices.Luxtronik.Enums;

namespace Namotion.Devices.Luxtronik.Gating;

/// <summary>
/// Firmware and function requirements of every register of a subject, for classes reused at several addresses.
/// </summary>
internal interface ILuxtronikGatedSubject
{
    Version? MinimumFirmwareVersion { get; }

    LuxtronikFunction Function { get; }
}
