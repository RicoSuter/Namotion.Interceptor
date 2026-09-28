// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Target and limits of a mixing circuit (inputs 10141 to 10143, 10151 to 10153, 10161 to 10163).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikMixingCircuitSetpoints : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    private readonly LuxtronikFeature _feature;

    /// <summary>
    /// Initializes the setpoints at <paramref name="baseAddress"/> whose registers require <paramref name="feature"/>.
    /// </summary>
    public LuxtronikMixingCircuitSetpoints(int baseAddress, LuxtronikFeature feature)
    {
        BaseAddress = baseAddress;
        _feature = feature;
        Target = null;
        Minimum = null;
        Maximum = null;
    }

    public int BaseAddress { get; }

    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal? Target { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? Minimum { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 3)]
    public partial decimal? Maximum { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => null;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => _feature;
}
