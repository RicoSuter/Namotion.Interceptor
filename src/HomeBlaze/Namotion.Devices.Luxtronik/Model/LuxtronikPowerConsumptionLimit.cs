// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// The electrical power consumption limit (LPC, holding 10040 and 10041). The limit is in watts; the controller reports kW in tenths.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikPowerConsumptionLimit : IModbusBaseAddressProvider
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikPowerConsumptionLimit()
    {
        Mode = null;
        Limit = null;
    }

    /// <inheritdoc />
    public int BaseAddress => 10040;

    /// <summary>
    /// Gets how the electrical power consumption is limited.
    /// </summary>
    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikPowerConsumptionLimitMode? Mode { get; internal set; }

    /// <summary>
    /// Gets the electrical power limit.
    /// </summary>
    [LuxtronikHoldingRegister(1, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 2)]
    public partial decimal? Limit { get; internal set; }
}
