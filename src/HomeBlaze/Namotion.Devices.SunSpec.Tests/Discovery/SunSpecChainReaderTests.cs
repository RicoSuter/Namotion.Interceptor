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

        var chunks = reads.Where(read => read.Address is >= 40004 and < 40304).ToList();
        Assert.True(chunks.Count > 1);
        var nextAddress = 40004;
        foreach (var chunk in chunks)
        {
            Assert.Equal(nextAddress, chunk.Address);
            nextAddress += chunk.Count;
        }

        Assert.Equal(40304, nextAddress);
    }

    [Fact]
    public async Task WhenSeveralBasesHaveAMarker_ThenTheMarkerAt40000Wins()
    {
        // Arrange
        var readers = new[] { 0, 50000, 40000 }
            .Select(markerAddress => InMemoryRegisters.CreateReader(new SunSpecTestChain(markerAddress).AddModel(1)))
            .ToArray();
        SunSpecRegisterReader reader = async (address, count, cancellationToken) =>
        {
            foreach (var inner in readers)
            {
                if (await inner(address, count, cancellationToken) is { } registers)
                {
                    return registers;
                }
            }

            return null;
        };

        // Act
        var result = await SunSpecChainReader.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal(40000, result!.MarkerAddress);
    }

    [Fact]
    public async Task WhenAMarkerProbeReturnsTheWrongRegisterCount_ThenTheNextBaseIsTried()
    {
        // Arrange
        var inner = InMemoryRegisters.CreateReader(new SunSpecTestChain(50000).AddModel(1));
        SunSpecRegisterReader reader = async (address, count, cancellationToken)
            => address == 40000 ? [0x5375] : await inner(address, count, cancellationToken);

        // Act
        var result = await SunSpecChainReader.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal(50000, result!.MarkerAddress);
        Assert.Equal(1, Assert.Single(result.Models).ModelId);
    }

    [Fact]
    public async Task WhenEndModelHasNoLengthRegister_ThenTheChainEndsNormally()
    {
        // Arrange
        var chain = new SunSpecTestChain().AddModel(1).AddModel(103);
        var endAddress = chain.NextAddress;
        var inner = InMemoryRegisters.CreateReader(chain);
        SunSpecRegisterReader reader = (address, count, cancellationToken)
            => address == endAddress && count == 2 ? Task.FromResult<ushort[]?>(null) : inner(address, count, cancellationToken);

        // Act
        var result = await SunSpecChainReader.ReadAsync(reader, CancellationToken.None);

        // Assert
        Assert.Equal(new[] { 1, 103 }, result!.Models.Select(model => model.ModelId));
    }

    [Fact]
    public async Task WhenEndModelHeaderAndIdReadsAreRejected_ThenTheChainIsMalformed()
    {
        // Arrange
        var chain = new SunSpecTestChain().AddModel(1);
        var endAddress = chain.NextAddress;
        var inner = InMemoryRegisters.CreateReader(chain);
        SunSpecRegisterReader reader = (address, count, cancellationToken)
            => address == endAddress ? Task.FromResult<ushort[]?>(null) : inner(address, count, cancellationToken);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => SunSpecChainReader.ReadAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task WhenHeaderReadIsRejectedButTheIdIsNoEndModel_ThenTheChainIsMalformed()
    {
        // Arrange
        var chain = new SunSpecTestChain().AddModel(1).AddModel(103);
        var inner = InMemoryRegisters.CreateReader(chain);
        SunSpecRegisterReader reader = (address, count, cancellationToken)
            => address == 40070 && count == 2 ? Task.FromResult<ushort[]?>(null) : inner(address, count, cancellationToken);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => SunSpecChainReader.ReadAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task WhenChainReachesTheEndOfTheAddressSpaceWithoutEndModel_ThenTheChainIsMalformed()
    {
        // Arrange
        var registers = new ushort[65536 - 50002];
        registers[0] = 64950;
        registers[1] = (ushort)(registers.Length - 2);
        var chain = new SunSpecTestChain(50000).AddRawModel(registers);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => InMemoryRegisters.ReadAsync(chain));
        Assert.Contains("at 65536", exception.Message);
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
