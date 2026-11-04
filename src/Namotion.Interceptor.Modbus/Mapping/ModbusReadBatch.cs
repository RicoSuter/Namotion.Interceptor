namespace Namotion.Interceptor.Modbus.Mapping;

/// <summary>
/// Registers or bits from <see cref="StartAddress"/> read in one request, or, for a single binding larger than one
/// request, in <see cref="RequestCount"/> consecutive requests.
/// </summary>
internal sealed class ModbusReadBatch
{
    public ModbusReadBatch(byte unitId, ModbusAddressSpace space, int startAddress, int count, ModbusRegisterBinding[] bindings)
    {
        UnitId = unitId;
        AddressSpace = space;
        StartAddress = startAddress;
        Count = count;
        Bindings = bindings;
        RequestCount = space.IsBitSpace()
            ? 1
            : (count + ModbusReadPlanner.MaximumRegistersPerRequest - 1) / ModbusReadPlanner.MaximumRegistersPerRequest;
    }

    public byte UnitId { get; }

    public ModbusAddressSpace AddressSpace { get; }

    public int StartAddress { get; }

    public int Count { get; }

    public ModbusRegisterBinding[] Bindings { get; }

    /// <summary>
    /// Gets the number of requests reading this batch. More than one only for a batch of a single binding.
    /// </summary>
    public int RequestCount { get; }

    /// <summary>
    /// Gets the start address and register count of the request at <paramref name="index"/>.
    /// </summary>
    public (int StartAddress, int Count) GetRequest(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RequestCount);
        if (RequestCount == 1)
        {
            return (StartAddress, Count);
        }

        var offset = index * ModbusReadPlanner.MaximumRegistersPerRequest;
        return (StartAddress + offset, Math.Min(ModbusReadPlanner.MaximumRegistersPerRequest, Count - offset));
    }
}
