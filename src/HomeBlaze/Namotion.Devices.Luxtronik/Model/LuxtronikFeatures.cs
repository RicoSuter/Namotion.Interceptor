// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Which controller functions are active (discrete inputs 10000 to 10011). Heating, cooling and the mixing circuit flags follow the operating modes, the others the controller configuration.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikFeatures : IModbusBaseAddressProvider
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikFeatures()
    {
        Heating = null;
        HotWater = null;
        Cooling = null;
        Pool = null;
        Solar = null;
        RoomControlUnit = null;
        MixingCircuit1Heating = null;
        MixingCircuit1Cooling = null;
        MixingCircuit2Heating = null;
        MixingCircuit2Cooling = null;
        MixingCircuit3Heating = null;
        MixingCircuit3Cooling = null;
    }

    /// <inheritdoc />
    public int BaseAddress => 10000;

    /// <summary>
    /// Gets the flags as a bit mask indexed by <see cref="Enums.LuxtronikFeature"/>, or <c>null</c> while a flag is unknown.
    /// </summary>
    internal int? GetFeatureMask()
    {
        ReadOnlySpan<bool?> values =
        [
            Heating, HotWater, Cooling, Pool, Solar, RoomControlUnit,
            MixingCircuit1Heating, MixingCircuit1Cooling, MixingCircuit2Heating,
            MixingCircuit2Cooling, MixingCircuit3Heating, MixingCircuit3Cooling
        ];

        Span<bool> flags = stackalloc bool[LuxtronikGating.FeatureFlagCount];
        for (var index = 0; index < flags.Length; index++)
        {
            if (values[index] is not { } flag)
            {
                return null;
            }

            flags[index] = flag;
        }

        return LuxtronikGating.GetFeatureMask(flags);
    }

    /// <summary>
    /// Gets whether the heating operating mode is not off.
    /// </summary>
    [ModbusRegister(0, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Heating { get; internal set; }

    /// <summary>
    /// Gets whether hot water is configured.
    /// </summary>
    [ModbusRegister(1, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? HotWater { get; internal set; }

    /// <summary>
    /// Gets whether the cooling operating mode is automatic.
    /// </summary>
    [ModbusRegister(2, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 3)]
    public partial bool? Cooling { get; internal set; }

    /// <summary>
    /// Gets whether pool heating is configured.
    /// </summary>
    [ModbusRegister(3, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 4)]
    public partial bool? Pool { get; internal set; }

    /// <summary>
    /// Gets whether solar is configured.
    /// </summary>
    [ModbusRegister(4, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 5)]
    public partial bool? Solar { get; internal set; }

    /// <summary>
    /// Gets whether a room control unit is configured.
    /// </summary>
    [ModbusRegister(5, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 6)]
    public partial bool? RoomControlUnit { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 1 is configured for heating and the heating operating mode is not off.
    /// </summary>
    [ModbusRegister(6, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 7)]
    public partial bool? MixingCircuit1Heating { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 1 is configured for cooling and the cooling operating mode is automatic.
    /// </summary>
    [ModbusRegister(7, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 8)]
    public partial bool? MixingCircuit1Cooling { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 2 is configured for heating and the heating operating mode is not off.
    /// </summary>
    [ModbusRegister(8, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 9)]
    public partial bool? MixingCircuit2Heating { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 2 is configured for cooling and the cooling operating mode is automatic.
    /// </summary>
    [ModbusRegister(9, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 10)]
    public partial bool? MixingCircuit2Cooling { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 3 is configured for heating and the heating operating mode is not off.
    /// </summary>
    [ModbusRegister(10, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 11)]
    public partial bool? MixingCircuit3Heating { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 3 is configured for cooling and the cooling operating mode is automatic.
    /// </summary>
    [ModbusRegister(11, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 12)]
    public partial bool? MixingCircuit3Cooling { get; internal set; }
}
