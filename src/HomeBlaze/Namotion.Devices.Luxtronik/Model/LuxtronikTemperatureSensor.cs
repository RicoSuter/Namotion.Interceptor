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
public partial class LuxtronikTemperatureSensor : ITemperatureSensor, ITitleProvider, IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private readonly Version? _minimumFirmwareVersion;
    private readonly LuxtronikFeature _feature;
    private readonly LuxtronikFeature _alternativeFeature;

    /// <summary>
    /// Initializes a sensor titled <paramref name="title"/> for the input register at <paramref name="address"/>, supported from <paramref name="minimumFirmware"/> and only when <paramref name="feature"/> or <paramref name="alternativeFeature"/> is configured.
    /// </summary>
    public LuxtronikTemperatureSensor(
        int address,
        string title,
        string? minimumFirmware = null,
        LuxtronikFeature feature = LuxtronikFeature.None,
        LuxtronikFeature alternativeFeature = LuxtronikFeature.None)
    {
        BaseAddress = address;
        Title = title;
        _minimumFirmwareVersion = minimumFirmware is null ? null : Version.Parse(minimumFirmware);
        _feature = feature;
        _alternativeFeature = alternativeFeature;
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

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => _minimumFirmwareVersion;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => _feature;

    LuxtronikFeature ILuxtronikGatedSubject.AlternativeFeature => _alternativeFeature;
}
