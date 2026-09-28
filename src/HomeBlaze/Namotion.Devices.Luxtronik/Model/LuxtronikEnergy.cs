// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Power and energy (inputs 10300 to 10329) in watts and watt-hours. The controller reports kW and kWh in tenths.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikEnergy : IModbusBaseAddressProvider
{
    public LuxtronikEnergy()
    {
        HeatingPower = null;
        ElectricalPower = null;
        MinimumPredictedElectricalPower = null;
        TotalElectricalEnergy = null;
        HeatingElectricalEnergy = null;
        HotWaterElectricalEnergy = null;
        CoolingElectricalEnergy = null;
        PoolElectricalEnergy = null;
        TotalThermalEnergy = null;
        HeatingThermalEnergy = null;
        HotWaterThermalEnergy = null;
        CoolingThermalEnergy = null;
        PoolThermalEnergy = null;
    }

    public int BaseAddress => 10300;

    /// <summary>
    /// Gets the thermal power currently produced.
    /// </summary>
    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 1)]
    public partial decimal? HeatingPower { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 2)]
    public partial decimal? ElectricalPower { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 3)]
    public partial decimal? MinimumPredictedElectricalPower { get; internal set; }

    [LuxtronikInputRegister(10, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 10)]
    public partial decimal? TotalElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(12, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 11)]
    public partial decimal? HeatingElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(14, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 12)]
    public partial decimal? HotWaterElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(16, ModbusDataType.S32, Scale = 100, Feature = LuxtronikFeature.Cooling)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 13)]
    public partial decimal? CoolingElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(18, ModbusDataType.S32, Scale = 100, Feature = LuxtronikFeature.Pool)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 14)]
    public partial decimal? PoolElectricalEnergy { get; internal set; }

    [LuxtronikInputRegister(20, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 20)]
    public partial decimal? TotalThermalEnergy { get; internal set; }

    [LuxtronikInputRegister(22, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 21)]
    public partial decimal? HeatingThermalEnergy { get; internal set; }

    [LuxtronikInputRegister(24, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 22)]
    public partial decimal? HotWaterThermalEnergy { get; internal set; }

    [LuxtronikInputRegister(26, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0", Feature = LuxtronikFeature.Cooling)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 23)]
    public partial decimal? CoolingThermalEnergy { get; internal set; }

    [LuxtronikInputRegister(28, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0", Feature = LuxtronikFeature.Pool)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 24)]
    public partial decimal? PoolThermalEnergy { get; internal set; }
}
