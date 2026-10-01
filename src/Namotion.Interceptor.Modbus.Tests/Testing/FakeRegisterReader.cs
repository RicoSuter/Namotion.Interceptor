using System.Buffers.Binary;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Modbus.Client.Transport;

namespace Namotion.Interceptor.Modbus.Tests.Testing;

internal sealed class FakeRegisterReader : IModbusRegisterReader
{
    private readonly Dictionary<(byte UnitId, ModbusAddressSpace AddressSpace, int Address), ushort> _registers = [];
    private readonly Dictionary<(byte UnitId, ModbusAddressSpace AddressSpace, int Address), bool> _bits = [];
    private readonly Dictionary<(byte UnitId, ModbusAddressSpace AddressSpace, int Address), int> _rejected = [];

    public List<(byte UnitId, ModbusAddressSpace AddressSpace, int Address, int Count)> Requests { get; } = [];

    public Exception? ConnectionFailure { get; set; }

    /// <summary>
    /// Gets or sets the zero-based index of the first request that fails with <see cref="ConnectionFailure"/>.
    /// </summary>
    public int ConnectionFailureFromRequest { get; set; }

    public void SetRegister(int address, ushort value, ModbusAddressSpace space = ModbusAddressSpace.HoldingRegister, byte unitId = 1)
        => _registers[(unitId, space, address)] = value;

    public void SetBit(int address, bool value, ModbusAddressSpace space = ModbusAddressSpace.Coil, byte unitId = 1)
        => _bits[(unitId, space, address)] = value;

    public void Reject(int address, ModbusAddressSpace space = ModbusAddressSpace.HoldingRegister, byte unitId = 1, int exceptionCode = 2)
        => _rejected[(unitId, space, address)] = exceptionCode;

    public void Accept(int address, ModbusAddressSpace space = ModbusAddressSpace.HoldingRegister, byte unitId = 1)
        => _rejected.Remove((unitId, space, address));

    public Task<ReadOnlyMemory<byte>> ReadAsync(
        byte unitId, ModbusAddressSpace space, int address, int count, CancellationToken cancellationToken)
    {
        Requests.Add((unitId, space, address, count));
        if (ConnectionFailure is not null && Requests.Count > ConnectionFailureFromRequest)
        {
            return Task.FromException<ReadOnlyMemory<byte>>(ConnectionFailure);
        }

        for (var index = 0; index < count; index++)
        {
            if (_rejected.TryGetValue((unitId, space, address + index), out var exceptionCode))
            {
                return Task.FromException<ReadOnlyMemory<byte>>(
                    new ModbusResponseException(exceptionCode, $"Exception code {exceptionCode}", new InvalidOperationException()));
            }
        }

        if (space is ModbusAddressSpace.Coil or ModbusAddressSpace.DiscreteInput)
        {
            var bits = new byte[(count + 7) / 8];
            for (var index = 0; index < count; index++)
            {
                if (_bits.GetValueOrDefault((unitId, space, address + index)))
                {
                    bits[index / 8] |= (byte)(1 << (index % 8));
                }
            }

            return Task.FromResult<ReadOnlyMemory<byte>>(bits);
        }

        var registers = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(registers.AsSpan(index * 2), _registers.GetValueOrDefault((unitId, space, address + index)));
        }

        return Task.FromResult<ReadOnlyMemory<byte>>(registers);
    }
}
