// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Power and energy totals of the heat pump over all functions, in watts and watt-hours.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikEnergy
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikEnergy()
    {
        ThermalPower = null;
        ElectricalPower = null;
        MinimumPredictedElectricalPower = null;
        TotalElectricalEnergy = null;
        TotalThermalEnergy = null;
    }

    /// <summary>
    /// Gets the current heat output (manual: Heizleistung IST).
    /// </summary>
    [LuxtronikInputRegister(10300, ModbusDataType.S16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 1)]
    public partial decimal? ThermalPower { get; internal set; }

    /// <summary>
    /// Gets the electrical power currently consumed.
    /// </summary>
    [LuxtronikInputRegister(10301, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 2)]
    public partial decimal? ElectricalPower { get; internal set; }

    /// <summary>
    /// Gets the predicted minimum electrical power consumption.
    /// </summary>
    [LuxtronikInputRegister(10302, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 3)]
    public partial decimal? MinimumPredictedElectricalPower { get; internal set; }

    /// <summary>
    /// Gets the total electrical energy consumed.
    /// </summary>
    [LuxtronikInputRegister(10310, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 4)]
    public partial decimal? TotalElectricalEnergy { get; internal set; }

    /// <summary>
    /// Gets the total thermal energy produced.
    /// </summary>
    [LuxtronikInputRegister(10320, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 5)]
    public partial decimal? TotalThermalEnergy { get; internal set; }
}
