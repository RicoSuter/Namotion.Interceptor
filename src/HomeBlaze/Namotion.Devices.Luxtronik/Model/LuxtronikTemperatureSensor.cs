// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// One measured temperature. Its <see cref="BaseAddress"/> is the input register address itself.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikTemperatureSensor : ITemperatureSensor, ITitleProvider, IModbusBaseAddressProvider, ILuxtronikRequirements
{
    private readonly Version? _minimumFirmwareVersion;

    /// <summary>
    /// Initializes a sensor titled <paramref name="title"/> for the input register at <paramref name="address"/>, supported from <paramref name="minimumFirmwareVersion"/>, or every firmware when <c>null</c>.
    /// </summary>
    public LuxtronikTemperatureSensor(int address, string title, Version? minimumFirmwareVersion = null)
    {
        BaseAddress = address;
        Title = title;
        _minimumFirmwareVersion = minimumFirmwareVersion;
        Temperature = null;
    }

    /// <inheritdoc />
    public int BaseAddress { get; }

    /// <inheritdoc />
    public string? Title { get; }

    // S16 for every temperature: the registers the manual types as UINT16 decode the same below 3276.7 degrees.
    /// <inheritdoc />
    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    Version? ILuxtronikRequirements.MinimumFirmwareVersion => _minimumFirmwareVersion;

    LuxtronikFunction ILuxtronikRequirements.RequiredFunction => LuxtronikFunction.None;
}
