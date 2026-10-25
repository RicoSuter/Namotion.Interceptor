using Namotion.Devices.Luxtronik.Enums;

namespace Namotion.Devices.Luxtronik.Gating;

/// <summary>
/// Firmware and function requirements of a register, declared on its register attribute, or of every register of a
/// subject, for classes reused at several addresses.
/// </summary>
internal interface ILuxtronikRequirements
{
    /// <summary>
    /// Gets the first firmware version providing the registers, or <c>null</c> for every firmware.
    /// </summary>
    Version? MinimumFirmwareVersion { get; }

    /// <summary>
    /// Gets the controller function that must be active for the registers to be read; <see cref="LuxtronikFunction.None"/> means no requirement.
    /// </summary>
    LuxtronikFunction RequiredFunction { get; }
}
