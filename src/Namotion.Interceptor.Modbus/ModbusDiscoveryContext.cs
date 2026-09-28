using System.Buffers.Binary;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Modbus.Mapping;
using Namotion.Interceptor.Modbus.Transport;

namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Raw access to the connected device for <see cref="IModbusDiscovery.DiscoverAsync"/>. Only valid while it runs.
/// </summary>
/// <remarks>
/// Not thread-safe: await each call before starting the next.
/// </remarks>
public sealed class ModbusDiscoveryContext
{
    private readonly IModbusRegisterReader _reader;
    private readonly byte _defaultUnitId;
    private readonly HashSet<PropertyReference> _excludedProperties = new(PropertyReference.Comparer);
    private bool _isInvalidated;

    internal ModbusDiscoveryContext(ISubjectSource source, IModbusRegisterReader reader, byte defaultUnitId)
    {
        Source = source;
        _reader = reader;
        _defaultUnitId = defaultUnitId;
    }

    /// <summary>
    /// Gets the source, for applying values the discovery reads with <c>SetValueFromSource</c>.
    /// </summary>
    public ISubjectSource Source { get; }

    internal IReadOnlySet<PropertyReference> ExcludedProperties => _excludedProperties;

    /// <summary>
    /// Reads 1 to 125 holding registers, from the configured unit unless <paramref name="unitId"/> is given.
    /// </summary>
    /// <exception cref="ModbusResponseException">The device rejected the request.</exception>
    public async Task<ushort[]> ReadHoldingRegistersAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default)
        => ToRegisters(await ReadAsync(ModbusAddressSpace.HoldingRegister, address, count, ModbusReadPlanner.MaximumRegistersPerRequest, unitId, cancellationToken).ConfigureAwait(false), count);

    /// <summary>
    /// Reads 1 to 125 input registers, from the configured unit unless <paramref name="unitId"/> is given.
    /// </summary>
    /// <exception cref="ModbusResponseException">The device rejected the request.</exception>
    public async Task<ushort[]> ReadInputRegistersAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default)
        => ToRegisters(await ReadAsync(ModbusAddressSpace.InputRegister, address, count, ModbusReadPlanner.MaximumRegistersPerRequest, unitId, cancellationToken).ConfigureAwait(false), count);

    /// <summary>
    /// Reads 1 to 2000 coils, from the configured unit unless <paramref name="unitId"/> is given.
    /// </summary>
    /// <exception cref="ModbusResponseException">The device rejected the request.</exception>
    public async Task<bool[]> ReadCoilsAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default)
        => ToBits(await ReadAsync(ModbusAddressSpace.Coil, address, count, ModbusReadPlanner.MaximumBitsPerRequest, unitId, cancellationToken).ConfigureAwait(false), count);

    /// <summary>
    /// Reads 1 to 2000 discrete inputs, from the configured unit unless <paramref name="unitId"/> is given.
    /// </summary>
    /// <exception cref="ModbusResponseException">The device rejected the request.</exception>
    public async Task<bool[]> ReadDiscreteInputsAsync(int address, int count, byte? unitId = null, CancellationToken cancellationToken = default)
        => ToBits(await ReadAsync(ModbusAddressSpace.DiscreteInput, address, count, ModbusReadPlanner.MaximumBitsPerRequest, unitId, cancellationToken).ConfigureAwait(false), count);

    /// <summary>
    /// Excludes a mapped property from this connection's read plan: it is neither claimed nor read. Has no effect for a property the connector does not map.
    /// </summary>
    public void ExcludeProperty(PropertyReference property)
    {
        ThrowIfInvalidated();
        _excludedProperties.Add(property);
    }

    internal void Invalidate() => _isInvalidated = true;

    private Task<ReadOnlyMemory<byte>> ReadAsync(
        ModbusAddressSpace space, int address, int count, int maximumCount, byte? unitId, CancellationToken cancellationToken)
    {
        ThrowIfInvalidated();
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, maximumCount);
        ArgumentOutOfRangeException.ThrowIfNegative(address);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(address, 65536 - count);
        return _reader.ReadAsync(unitId ?? _defaultUnitId, space, address, count, cancellationToken);
    }

    private static ushort[] ToRegisters(ReadOnlyMemory<byte> data, int count)
    {
        var registers = new ushort[count];
        var span = data.Span;
        for (var index = 0; index < count; index++)
        {
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(span[(index * 2)..]);
        }

        return registers;
    }

    private static bool[] ToBits(ReadOnlyMemory<byte> data, int count)
    {
        var bits = new bool[count];
        var span = data.Span;
        for (var index = 0; index < count; index++)
        {
            bits[index] = ((span[index / 8] >> (index % 8)) & 1) != 0;
        }

        return bits;
    }

    private void ThrowIfInvalidated()
    {
        if (_isInvalidated)
        {
            throw new ObjectDisposedException(nameof(ModbusDiscoveryContext), "The context is only valid while DiscoverAsync runs.");
        }
    }
}
