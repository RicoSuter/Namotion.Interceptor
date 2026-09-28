namespace Namotion.Interceptor.Modbus.Mapping;

/// <summary>
/// One read request covering <see cref="Count"/> registers or bits from <see cref="StartAddress"/>.
/// </summary>
internal sealed class ModbusReadBatch
{
    public ModbusReadBatch(byte unitId, ModbusAddressSpace space, int startAddress, int count, ModbusRegisterBinding[] bindings)
    {
        UnitId = unitId;
        Space = space;
        StartAddress = startAddress;
        Count = count;
        Bindings = bindings;
    }

    public byte UnitId { get; }

    public ModbusAddressSpace Space { get; }

    public int StartAddress { get; }

    public int Count { get; }

    public ModbusRegisterBinding[] Bindings { get; }
}
