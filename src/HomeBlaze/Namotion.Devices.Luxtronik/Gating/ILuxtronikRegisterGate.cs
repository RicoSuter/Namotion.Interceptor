namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Firmware and feature requirements declared on a register attribute.
/// </summary>
internal interface ILuxtronikRegisterGate
{
    Version? MinimumFirmwareVersion { get; }

    LuxtronikFeature Feature { get; }
}
