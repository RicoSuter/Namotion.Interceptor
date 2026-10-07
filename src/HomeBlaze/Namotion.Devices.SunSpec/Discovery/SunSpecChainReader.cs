namespace Namotion.Devices.SunSpec.Discovery;

/// <summary>
/// Reads <paramref name="count"/> holding registers at <paramref name="address"/>, or returns <c>null</c> when the
/// device permanently rejects the request (Modbus exception codes 1 to 3).
/// </summary>
internal delegate Task<ushort[]?> SunSpecRegisterReader(int address, int count, CancellationToken cancellationToken);

/// <summary>
/// One model of a chain with all its registers, starting with the ID and length registers.
/// </summary>
internal sealed record SunSpecChainEntry(int ModelId, int Address, int Length, ushort[] Registers);

/// <summary>
/// The model chain of one unit.
/// </summary>
internal sealed record SunSpecChain(int MarkerAddress, IReadOnlyList<SunSpecChainEntry> Models);

/// <summary>
/// Finds the "SunS" marker and walks the model chain behind it.
/// </summary>
internal static class SunSpecChainReader
{
    /// <summary>
    /// The maximum number of models in one chain; more means the chain is malformed.
    /// </summary>
    public const int MaximumModelCount = 500;

    private const int MaximumRegistersPerRead = 125;
    private const ushort EndModelId = 0xFFFF;
    private const int AddressCount = 65536;

    // The base addresses the SunSpec specification allows for the marker, in the order they are tried.
    private static readonly int[] MarkerAddresses = [40000, 50000, 0];

    /// <summary>
    /// Reads the chain, or returns <c>null</c> when no allowed base address holds the "SunS" marker.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The chain is malformed: it overruns the address space, has more than <see cref="MaximumModelCount"/> models, or a
    /// read inside it is rejected or returns the wrong number of registers.
    /// </exception>
    public static async Task<SunSpecChain?> ReadAsync(SunSpecRegisterReader read, CancellationToken cancellationToken)
    {
        foreach (var markerAddress in MarkerAddresses)
        {
            var marker = await read(markerAddress, 2, cancellationToken).ConfigureAwait(false);
            if (marker is [0x5375, 0x6E53])
            {
                var models = await ReadModelsAsync(read, markerAddress + 2, cancellationToken).ConfigureAwait(false);
                return new SunSpecChain(markerAddress, models);
            }
        }

        return null;
    }

    // Every model advances the address by at least its two header registers and must end inside the address space,
    // so the walk terminates and the total allocation is bounded by the address space.
    private static async Task<IReadOnlyList<SunSpecChainEntry>> ReadModelsAsync(SunSpecRegisterReader read, int address, CancellationToken cancellationToken)
    {
        var models = new List<SunSpecChainEntry>();
        while (true)
        {
            if (address + 2 > AddressCount)
            {
                throw new InvalidDataException($"The model chain runs past address 65535 at {address}.");
            }

            var header = await ReadRequiredAsync(read, address, 2, cancellationToken).ConfigureAwait(false);
            if (header[0] == EndModelId)
            {
                return models;
            }

            if (models.Count == MaximumModelCount)
            {
                throw new InvalidDataException($"The model chain has more than {MaximumModelCount} models.");
            }

            var length = header[1];
            var end = address + 2 + length;
            if (end > AddressCount)
            {
                throw new InvalidDataException($"Model {header[0]} at {address} with length {length} runs past address 65535.");
            }

            var registers = new ushort[length + 2];
            registers[0] = header[0];
            registers[1] = header[1];
            for (var offset = 2; offset < registers.Length; offset += MaximumRegistersPerRead)
            {
                var count = Math.Min(MaximumRegistersPerRead, registers.Length - offset);
                var block = await ReadRequiredAsync(read, address + offset, count, cancellationToken).ConfigureAwait(false);
                block.CopyTo(registers, offset);
            }

            models.Add(new SunSpecChainEntry(header[0], address, length, registers));
            address = end;
        }
    }

    private static async Task<ushort[]> ReadRequiredAsync(SunSpecRegisterReader read, int address, int count, CancellationToken cancellationToken)
    {
        var registers = await read(address, count, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"The device rejected reading {count} registers at {address} inside the model chain.");

        if (registers.Length != count)
        {
            throw new InvalidDataException($"The device returned {registers.Length} instead of {count} registers at {address}.");
        }

        return registers;
    }
}
