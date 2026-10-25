using Namotion.Devices.SunSpec.Discovery;

namespace Namotion.Devices.SunSpec.Tests.Testing;

/// <summary>
/// Serves a test chain to the chain reader without a Modbus connection; reads outside the chain are rejected.
/// </summary>
internal static class InMemoryRegisters
{
    public static SunSpecRegisterReader CreateReader(SunSpecTestChain chain, List<(int Address, int Count)>? reads = null)
    {
        var registers = chain.Build();
        var start = chain.MarkerAddress;
        return (address, count, _) =>
        {
            reads?.Add((address, count));
            var isInside = address >= start && address + count <= start + registers.Length;
            return Task.FromResult<ushort[]?>(isInside ? registers.AsSpan(address - start, count).ToArray() : null);
        };
    }

    public static Task<SunSpecChain?> ReadAsync(SunSpecTestChain chain)
        => SunSpecChainReader.ReadAsync(CreateReader(chain), CancellationToken.None);
}
