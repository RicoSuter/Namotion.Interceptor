using HomeBlaze.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.SunSpec.Models;

/// <summary>
/// A model in the chain without a generated class or a definition, so only its position is known.
/// </summary>
[InterceptorSubject]
public partial class SunSpecUnknownModel : ISunSpecModel, ITitleProvider
{
    /// <summary>
    /// Initializes the model <paramref name="modelId"/> at <paramref name="baseAddress"/>, the address of its ID register,
    /// with <paramref name="length"/> registers after the ID and length registers.
    /// </summary>
    public SunSpecUnknownModel(int modelId, int baseAddress, int length)
    {
        ModelId = modelId;
        BaseAddress = baseAddress;
        Length = length;
        ModelIdRegister = null;
    }

    /// <inheritdoc />
    public int ModelId { get; }

    /// <inheritdoc />
    public int BaseAddress { get; }

    /// <inheritdoc />
    public int Length { get; }

    /// <inheritdoc />
    public string Title => $"Unknown model {ModelId}";

    /// <inheritdoc />
    [ModbusRegister(0, ModbusDataType.U16, Access = ModbusAccess.ReadOnly)]
    public partial ushort? ModelIdRegister { get; internal set; }
}
