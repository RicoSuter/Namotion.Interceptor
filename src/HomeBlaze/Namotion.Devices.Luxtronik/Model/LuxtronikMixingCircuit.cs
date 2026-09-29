// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Mixing circuit 1, 2 or 3. Registers of circuit n are 10 addresses after those of circuit n - 1.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikMixingCircuit : ITitleProvider
{
    /// <summary>
    /// Initializes mixing circuit <paramref name="index"/>, from 1 to 3.
    /// </summary>
    public LuxtronikMixingCircuit(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 3);

        Index = index;
        Title = $"Mixing circuit {index}";
        var offset = (index - 1) * 10;
        var heatingFeature = LuxtronikFeature.MixingCircuit1Heating + (index - 1) * 2;
        var coolingFeature = heatingFeature + 1;

        Temperature = new LuxtronikTemperatureSensor(10140 + offset, $"Mixing circuit {index} temperature", feature: heatingFeature);
        Setpoints = new LuxtronikMixingCircuitSetpoints(10141 + offset, heatingFeature);
        Heating = new LuxtronikControl(10010 + offset, heatingFeature);
        Cooling = new LuxtronikCoolingControl(10015 + offset, coolingFeature);
    }

    /// <summary>
    /// Gets the circuit number, from 1 to 3.
    /// </summary>
    public int Index { get; }

    /// <inheritdoc />
    public string? Title { get; }

    /// <summary>
    /// Gets the measured circuit temperature.
    /// </summary>
    [State(Position = 1)]
    public partial LuxtronikTemperatureSensor Temperature { get; internal set; }

    /// <summary>
    /// Gets the circuit target temperature and its limits.
    /// </summary>
    [State(Position = 2)]
    public partial LuxtronikMixingCircuitSetpoints Setpoints { get; internal set; }

    /// <summary>
    /// Gets the heating control of the circuit.
    /// </summary>
    [State(Position = 3)]
    public partial LuxtronikControl Heating { get; internal set; }

    /// <summary>
    /// Gets the cooling control of the circuit.
    /// </summary>
    [State(Position = 4)]
    public partial LuxtronikCoolingControl Cooling { get; internal set; }
}
