// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Which functions are switched on or configured on the controller (discrete inputs 10000 to 10011). Heating, hot water, cooling, pool and the mixing circuit flags follow the operating modes; solar and the room control unit follow the controller configuration.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikFunctions : IModbusBaseAddressProvider
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikFunctions()
    {
        IsHeatingEnabled = null;
        IsHotWaterEnabled = null;
        IsCoolingEnabled = null;
        IsPoolEnabled = null;
        IsSolarConfigured = null;
        IsRoomControlUnitConfigured = null;
        IsMixingCircuit1HeatingEnabled = null;
        IsMixingCircuit1CoolingEnabled = null;
        IsMixingCircuit2HeatingEnabled = null;
        IsMixingCircuit2CoolingEnabled = null;
        IsMixingCircuit3HeatingEnabled = null;
        IsMixingCircuit3CoolingEnabled = null;
    }

    /// <inheritdoc />
    public int BaseAddress => 10000;

    /// <summary>
    /// Gets the flags as a bit mask indexed by <see cref="Enums.LuxtronikFunction"/>, or <c>null</c> while a flag is unknown.
    /// </summary>
    internal int? GetFunctionMask()
    {
        ReadOnlySpan<bool?> values =
        [
            IsHeatingEnabled, IsHotWaterEnabled, IsCoolingEnabled, IsPoolEnabled,
            IsSolarConfigured, IsRoomControlUnitConfigured,
            IsMixingCircuit1HeatingEnabled, IsMixingCircuit1CoolingEnabled,
            IsMixingCircuit2HeatingEnabled, IsMixingCircuit2CoolingEnabled,
            IsMixingCircuit3HeatingEnabled, IsMixingCircuit3CoolingEnabled
        ];

        Span<bool> flags = stackalloc bool[LuxtronikGating.FunctionFlagCount];
        for (var index = 0; index < flags.Length; index++)
        {
            if (values[index] is not { } flag)
            {
                return null;
            }

            flags[index] = flag;
        }

        return LuxtronikGating.GetFunctionMask(flags);
    }

    /// <summary>
    /// Gets whether the heating operating mode is switched on (not Aus).
    /// </summary>
    [ModbusRegister(0, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? IsHeatingEnabled { get; internal set; }

    /// <summary>
    /// Gets whether the hot water operating mode is switched on (not Aus).
    /// </summary>
    [ModbusRegister(1, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? IsHotWaterEnabled { get; internal set; }

    /// <summary>
    /// Gets whether the cooling operating mode is switched on (Automatisch).
    /// </summary>
    [ModbusRegister(2, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 3)]
    public partial bool? IsCoolingEnabled { get; internal set; }

    /// <summary>
    /// Gets whether the pool operating mode is switched on (Automatisch).
    /// </summary>
    [ModbusRegister(3, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 4)]
    public partial bool? IsPoolEnabled { get; internal set; }

    /// <summary>
    /// Gets whether solar is configured on the controller.
    /// </summary>
    [ModbusRegister(4, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 5)]
    public partial bool? IsSolarConfigured { get; internal set; }

    /// <summary>
    /// Gets whether a room control unit is configured on the controller.
    /// </summary>
    [ModbusRegister(5, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 6)]
    public partial bool? IsRoomControlUnitConfigured { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 1 is configured for heating and the heating operating mode is switched on (not Aus).
    /// </summary>
    [ModbusRegister(6, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 7)]
    public partial bool? IsMixingCircuit1HeatingEnabled { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 1 is configured for cooling and the cooling operating mode is switched on (Automatisch).
    /// </summary>
    [ModbusRegister(7, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 8)]
    public partial bool? IsMixingCircuit1CoolingEnabled { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 2 is configured for heating and the heating operating mode is switched on (not Aus).
    /// </summary>
    [ModbusRegister(8, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 9)]
    public partial bool? IsMixingCircuit2HeatingEnabled { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 2 is configured for cooling and the cooling operating mode is switched on (Automatisch).
    /// </summary>
    [ModbusRegister(9, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 10)]
    public partial bool? IsMixingCircuit2CoolingEnabled { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 3 is configured for heating and the heating operating mode is switched on (not Aus).
    /// </summary>
    [ModbusRegister(10, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 11)]
    public partial bool? IsMixingCircuit3HeatingEnabled { get; internal set; }

    /// <summary>
    /// Gets whether mixing circuit 3 is configured for cooling and the cooling operating mode is switched on (Automatisch).
    /// </summary>
    [ModbusRegister(11, ModbusDataType.Boolean, AddressSpace = ModbusAddressSpace.DiscreteInput)]
    [State(IsDiscrete = true, Position = 12)]
    public partial bool? IsMixingCircuit3CoolingEnabled { get; internal set; }
}
