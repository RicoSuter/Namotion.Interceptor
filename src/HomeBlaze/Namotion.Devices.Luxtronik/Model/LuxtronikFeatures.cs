// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Which controller functions are configured (discrete inputs 10000 to 10011).
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
    /// Gets whether heating is configured.
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
    /// Gets whether cooling is configured.
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
    /// Gets whether mixing circuit 1 is configured for heating.
    /// </summary>
    [ModbusRegister(6, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 7)]
    public partial bool? MixingCircuit1Heating { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 1 is configured for cooling.
    /// </summary>
    [ModbusRegister(7, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 8)]
    public partial bool? MixingCircuit1Cooling { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 2 is configured for heating.
    /// </summary>
    [ModbusRegister(8, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 9)]
    public partial bool? MixingCircuit2Heating { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 2 is configured for cooling.
    /// </summary>
    [ModbusRegister(9, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 10)]
    public partial bool? MixingCircuit2Cooling { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 3 is configured for heating.
    /// </summary>
    [ModbusRegister(10, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 11)]
    public partial bool? MixingCircuit3Heating { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 3 is configured for cooling.
    /// </summary>
    [ModbusRegister(11, ModbusDataType.Boolean, Space = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 12)]
    public partial bool? MixingCircuit3Cooling { get; internal set; }
}
