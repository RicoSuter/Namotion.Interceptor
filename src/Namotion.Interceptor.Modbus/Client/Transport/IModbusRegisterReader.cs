namespace Namotion.Interceptor.Modbus.Client.Transport;

internal interface IModbusRegisterReader
{
    /// <summary>
    /// Reads <paramref name="count"/> registers (two big-endian bytes each) or bits (packed, lowest address in
    /// bit 0 of byte 0).
    /// </summary>
    /// <remarks>
    /// The returned memory may be a pooled buffer that the next read overwrites; copy it before reading again.
    /// Throws <see cref="ModbusResponseException"/> for a Modbus exception response; any other exception means
    /// the connection is lost.
    /// </remarks>
    Task<ReadOnlyMemory<byte>> ReadAsync(
        byte unitId, ModbusAddressSpace space, int address, int count, CancellationToken cancellationToken);
}
