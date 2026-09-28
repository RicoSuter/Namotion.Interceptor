// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// One measured temperature. Its <see cref="BaseAddress"/> is the input register address itself.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikTemperatureSensor : ITemperatureSensor, ITitleProvider, IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private readonly string? _minimumFirmware;
    private readonly LuxtronikFeature _feature;

    public LuxtronikTemperatureSensor(int address, string title, string? minimumFirmware = null, LuxtronikFeature feature = LuxtronikFeature.None)
    {
        BaseAddress = address;
        Title = title;
        _minimumFirmware = minimumFirmware;
        _feature = feature;
        Temperature = null;
    }

    public int BaseAddress { get; }

    public string? Title { get; }

    // S16 for every temperature: the registers the manual types as UINT16 decode the same below 3276.7 degrees.
    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => _minimumFirmware;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => _feature;
}
