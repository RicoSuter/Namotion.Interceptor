// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Pool heating. Present while the pool flag is set (pool operating mode Automatisch).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikPool
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikPool()
    {
        Status = null;
        Locked = null;
        OperatingHours = null;
        ElectricalEnergy = null;
        ThermalEnergy = null;
    }

    /// <summary>
    /// Gets the state of pool heating.
    /// </summary>
    [LuxtronikInputRegister(10007, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikModeStatus? Status { get; internal set; }

    /// <summary>
    /// Gets whether pool heating is locked over the SHI.
    /// </summary>
    [LuxtronikHoldingRegister(10053, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? Locked { get; internal set; }

    /// <summary>
    /// Gets the operating hours in pool heating.
    /// </summary>
    [LuxtronikInputRegister(10412, ModbusDataType.U32, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 3)]
    public partial decimal? OperatingHours { get; internal set; }

    /// <summary>
    /// Gets the electrical energy consumed for pool heating.
    /// </summary>
    [LuxtronikInputRegister(10318, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 4)]
    public partial decimal? ElectricalEnergy { get; internal set; }

    /// <summary>
    /// Gets the thermal energy produced for pool heating.
    /// </summary>
    [LuxtronikInputRegister(10328, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 5)]
    public partial decimal? ThermalEnergy { get; internal set; }
}
