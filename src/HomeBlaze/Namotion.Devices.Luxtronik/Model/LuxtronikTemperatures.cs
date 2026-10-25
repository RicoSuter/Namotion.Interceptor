// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Temperatures of the heat pump itself, measured for every function, and its maximum flow temperature.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikTemperatures
{
    /// <summary>
    /// Initializes the sensors at their register addresses.
    /// </summary>
    public LuxtronikTemperatures()
    {
        Flow = new LuxtronikTemperatureSensor(10105, "Flow temperature");
        Return = new LuxtronikTemperatureSensor(10100, "Return temperature");
        Outside = new LuxtronikTemperatureSensor(10108, "Outside temperature");
        OutsideAverage = new LuxtronikTemperatureSensor(10109, "Outside average temperature", LuxtronikGating.Firmware392);
        HeatSourceInlet = new LuxtronikTemperatureSensor(10110, "Heat source inlet temperature", LuxtronikGating.Firmware392);
        HeatSourceOutlet = new LuxtronikTemperatureSensor(10111, "Heat source outlet temperature", LuxtronikGating.Firmware392);
        MaximumFlowTemperature = null;
    }

    /// <summary>
    /// Gets the measured flow temperature of the heat pump, whichever function it serves.
    /// </summary>
    [State(Position = 1)]
    public partial LuxtronikTemperatureSensor Flow { get; internal set; }

    /// <summary>
    /// Gets the measured return temperature of the heat pump, whichever function it serves.
    /// </summary>
    [State(Position = 2)]
    public partial LuxtronikTemperatureSensor Return { get; internal set; }

    /// <summary>
    /// Gets the measured outside temperature.
    /// </summary>
    [State(Position = 3)]
    public partial LuxtronikTemperatureSensor Outside { get; internal set; }

    /// <summary>
    /// Gets the average outside temperature of the last 24 hours.
    /// </summary>
    [State(Position = 4)]
    public partial LuxtronikTemperatureSensor OutsideAverage { get; internal set; }

    /// <summary>
    /// Gets the measured heat source inlet temperature.
    /// </summary>
    [State(Position = 5)]
    public partial LuxtronikTemperatureSensor HeatSourceInlet { get; internal set; }

    /// <summary>
    /// Gets the measured heat source outlet temperature.
    /// </summary>
    [State(Position = 6)]
    public partial LuxtronikTemperatureSensor HeatSourceOutlet { get; internal set; }

    /// <summary>
    /// Gets the maximum flow temperature; above it the compressor switches off, for every function.
    /// </summary>
    [LuxtronikInputRegister(10112, ModbusDataType.S16, Scale = 0.1, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 7)]
    public partial decimal? MaximumFlowTemperature { get; internal set; }
}
