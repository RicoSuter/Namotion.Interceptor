using Namotion.Devices.Luxtronik.Enums;

namespace Namotion.Devices.Luxtronik.Gating;

/// <summary>
/// Firmware and feature requirements declared on a register attribute.
/// </summary>
internal interface ILuxtronikRegisterGate
{
    Version? MinimumFirmwareVersion { get; }

    LuxtronikFeature Feature { get; }
}
