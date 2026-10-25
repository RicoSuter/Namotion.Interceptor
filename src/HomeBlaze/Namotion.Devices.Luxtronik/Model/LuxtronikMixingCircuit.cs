// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Mixing circuit 1, 2 or 3. Present while the circuit's heating or cooling flag is set. The register addresses are those of circuit 1; circuit n is shifted by its <see cref="BaseAddress"/> of (n - 1) * 10.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikMixingCircuit : ITitleProvider, IModbusBaseAddressProvider, ILuxtronikCircuitSubject
{
    private readonly int _functionOffset;

    /// <summary>
    /// Initializes mixing circuit <paramref name="index"/>, from 1 to 3.
    /// </summary>
    public LuxtronikMixingCircuit(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 3);

        Index = index;
        Title = $"Mixing circuit {index}";
        BaseAddress = (index - 1) * 10;
        _functionOffset = (index - 1) * 2;
        var heatingFunction = LuxtronikFunction.MixingCircuit1Heating + _functionOffset;
        var coolingFunction = heatingFunction + 1;

        Temperature = new LuxtronikTemperatureSensor(10140 + BaseAddress, $"Mixing circuit {index} temperature");
        Pump = new LuxtronikPump(10350 + index, $"Mixing circuit {index} pump (FP{index})");
        HeatingSmartHomeControl = new LuxtronikSmartHomeControl(10010 + BaseAddress, heatingFunction);
        CoolingSmartHomeControl = new LuxtronikCoolingSmartHomeControl(10015 + BaseAddress, coolingFunction);
        Target = null;
        MinimumTarget = null;
        MaximumTarget = null;
    }

    /// <summary>
    /// Gets the circuit number, from 1 to 3.
    /// </summary>
    public int Index { get; }

    /// <inheritdoc />
    public string? Title { get; }

    /// <inheritdoc />
    public int BaseAddress { get; }

    int ILuxtronikCircuitSubject.FunctionOffset => _functionOffset;

    /// <summary>
    /// Gets the flow target temperature of the circuit.
    /// </summary>
    [LuxtronikInputRegister(10141, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal? Target { get; internal set; }

    /// <summary>
    /// Gets the minimum flow temperature of the circuit, read while the circuit heats.
    /// </summary>
    [LuxtronikInputRegister(10142, ModbusDataType.S16, Scale = 0.1, RequiredFunction = LuxtronikFunction.MixingCircuit1Heating)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? MinimumTarget { get; internal set; }

    /// <summary>
    /// Gets the maximum flow temperature of the circuit, read while the circuit heats.
    /// </summary>
    [LuxtronikInputRegister(10143, ModbusDataType.S16, Scale = 0.1, RequiredFunction = LuxtronikFunction.MixingCircuit1Heating)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 3)]
    public partial decimal? MaximumTarget { get; internal set; }

    /// <summary>
    /// Gets the measured flow temperature of the circuit.
    /// </summary>
    [State(Position = 20)]
    public partial LuxtronikTemperatureSensor Temperature { get; internal set; }

    /// <summary>
    /// Gets the circulation pump output of the circuit (FP1 to FP3).
    /// </summary>
    [State(Position = 21)]
    public partial LuxtronikPump Pump { get; internal set; }

    /// <summary>
    /// Gets the heating setpoint configuration of the circuit sent over the SHI, read while the circuit heats.
    /// </summary>
    [State(Position = 22)]
    public partial LuxtronikSmartHomeControl HeatingSmartHomeControl { get; internal set; }

    /// <summary>
    /// Gets the cooling setpoint configuration of the circuit sent over the SHI, read while the circuit cools.
    /// </summary>
    [State(Position = 23)]
    public partial LuxtronikCoolingSmartHomeControl CoolingSmartHomeControl { get; internal set; }
}
