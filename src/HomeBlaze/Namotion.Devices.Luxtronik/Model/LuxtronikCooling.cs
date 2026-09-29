// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Cooling. Present while the cooling flag is set (cooling operating mode Automatisch).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikCooling
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikCooling()
    {
        Status = null;
        IsReleased = null;
        IsLocked = null;
        OperatingHours = null;
        ElectricalEnergy = null;
        ThermalEnergy = null;
    }

    /// <summary>
    /// Gets the state of cooling.
    /// </summary>
    [LuxtronikInputRegister(10006, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikModeStatus? Status { get; internal set; }

    /// <summary>
    /// Gets whether cooling is released (manual: Kühlfreigabe).
    /// </summary>
    [LuxtronikInputRegister(10207, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? IsReleased { get; internal set; }

    /// <summary>
    /// Gets whether cooling is locked over the SHI.
    /// </summary>
    [LuxtronikHoldingRegister(10052, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 3)]
    public partial bool? IsLocked { get; internal set; }

    /// <summary>
    /// Gets the operating hours of active cooling.
    /// </summary>
    [LuxtronikInputRegister(10410, ModbusDataType.U32, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 4)]
    public partial decimal? OperatingHours { get; internal set; }

    /// <summary>
    /// Gets the electrical energy consumed for cooling.
    /// </summary>
    [LuxtronikInputRegister(10316, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 5)]
    public partial decimal? ElectricalEnergy { get; internal set; }

    /// <summary>
    /// Gets the thermal energy produced for cooling.
    /// </summary>
    [LuxtronikInputRegister(10326, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 6)]
    public partial decimal? ThermalEnergy { get; internal set; }
}
