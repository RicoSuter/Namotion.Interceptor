using Namotion.Devices.SunSpec.Discovery;
using Namotion.Devices.SunSpec.Tests.Testing;

namespace Namotion.Devices.SunSpec.Tests.Discovery;

public class SunSpecChainReaderTests
{
    [Theory]
    [InlineData(40000)]
    [InlineData(50000)]
    [InlineData(0)]
    public async Task WhenMarkerIsAtAWellKnownAddress_ThenTheChainIsRead(int markerAddress)
    {
        // Arrange
        var chain = new SunSpecTestChain(markerAddress).AddModel(1).AddModel(103);

        // Act
        var result = await InMemoryRegisters.ReadAsync(chain);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(markerAddress, result.MarkerAddress);
        Assert.Equal(new[] { 1, 103 }, result.Models.Select(model => model.ModelId));
        Assert.Equal(markerAddress + 2, result.Models[0].Address);
        Assert.Equal(66, result.Models[0].Length);
        Assert.Equal(markerAddress + 2 + 68, result.Models[1].Address);
        Assert.Equal(52, result.Models[1].Registers.Length);
    }

    [Fact]
    public async Task WhenNoMarkerExists_ThenNoChainIsReturned()
    {
        // Arrange
        SunSpecRegisterReader reader = (_, _, _) => Task.FromResult<ushort[]?>(null);

        // Act
        var result = await SunSpecChainReader.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task WhenModelIsLongerThanOneRequest_ThenItIsReadInChunks()
    {
        // Arrange
        var registers = new ushort[302];
        registers[0] = 64950;
        registers[1] = 300;
        var chain = new SunSpecTestChain().AddRawModel(registers);
        var reads = new List<(int Address, int Count)>();

        // Act
        var result = await SunSpecChainReader.ReadAsync(InMemoryRegisters.CreateReader(chain, reads), CancellationToken.None);

        // Assert
        Assert.Equal(302, Assert.Single(result!.Models).Registers.Length);
        Assert.All(reads, read => Assert.InRange(read.Count, 1, 125));
    }

    [Fact]
    public async Task WhenModelRunsPastTheAddressSpace_ThenTheChainIsMalformed()
    {
        // Arrange
        var chain = new SunSpecTestChain(50000).AddRawModel([64950, 65535]);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryRegisters.ReadAsync(chain));
    }

    [Fact]
    public async Task WhenChainHasTooManyModels_ThenTheChainIsMalformed()
    {
        // Arrange
        var chain = new SunSpecTestChain();
        for (var index = 0; index <= SunSpecChainReader.MaximumModelCount; index++)
        {
            chain.AddRawModel([64950, 0]);
        }

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryRegisters.ReadAsync(chain));
    }

    [Fact]
    public async Task WhenAReadInsideTheChainIsRejected_ThenTheChainIsMalformed()
    {
        // Arrange
        var chain = new SunSpecTestChain().AddModel(1).AddModel(103);
        var inner = InMemoryRegisters.CreateReader(chain);
        SunSpecRegisterReader reader = (address, count, cancellationToken)
            => address == 40070 ? Task.FromResult<ushort[]?>(null) : inner(address, count, cancellationToken);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => SunSpecChainReader.ReadAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task WhenAReadReturnsTheWrongRegisterCount_ThenTheChainIsMalformed()
    {
        // Arrange
        var chain = new SunSpecTestChain().AddModel(1);
        var inner = InMemoryRegisters.CreateReader(chain);
        SunSpecRegisterReader reader = async (address, count, cancellationToken)
            => address == 40002 ? [1] : await inner(address, count, cancellationToken);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => SunSpecChainReader.ReadAsync(reader, CancellationToken.None));
    }
}
