using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec;

/// <summary>
/// A SunSpec model in the model chain of a unit. <see cref="IModbusBaseAddressProvider.BaseAddress"/> is the address
/// of the model's ID register, so register offsets equal the SunSpec offsets (ID = 0, L = 1, first point = 2).
/// </summary>
public interface ISunSpecModel : IInterceptorSubject, IModbusBaseAddressProvider
{
    /// <summary>
    /// Gets the SunSpec model ID.
    /// </summary>
    int ModelId { get; }

    /// <summary>
    /// Gets the model length in registers, excluding the ID and length registers.
    /// </summary>
    int Length { get; }

    /// <summary>
    /// Gets the model ID polled from the model's ID register, or <c>null</c> before it was read.
    /// </summary>
    ushort? ModelIdRegister { get; }
}
