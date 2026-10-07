namespace Namotion.Devices.SunSpec.Tests.Testing;

public class SunSpecTestChainTests
{
    [Fact]
    public void WhenAddingAModel_ThenValuesLandAtTheirSunSpecOffsets()
    {
        // Arrange
        var chain = new SunSpecTestChain().AddModel(103, new Dictionary<string, object?> { ["W"] = -800, ["W_SF"] = -1 });

        // Act
        var registers = chain.Build();

        // Assert: marker (2), then the model: ID at 0, L at 1, A (uint16) at 2, A_SF at 6, W at 14, W_SF at 15.
        Assert.Equal(new ushort[] { 0x5375, 0x6E53, 103, 50 }, registers[..4]);
        Assert.Equal(unchecked((ushort)-800), registers[2 + 14]);
        Assert.Equal(unchecked((ushort)-1), registers[2 + 15]);
        Assert.Equal(0xFFFF, registers[2 + 2]);
        Assert.Equal(0x8000, registers[2 + 6]);
        Assert.Equal(new ushort[] { 0xFFFF, 0 }, registers[^2..]);
    }

    [Fact]
    public void WhenLengthIsGiven_ThenTheModelIsTruncated()
    {
        // Act
        var registers = new SunSpecTestChain().AddModel(1, length: 65).Build();

        // Assert
        Assert.Equal(65, registers[3]);
        Assert.Equal(2 + 67 + 2, registers.Length);
    }

    [Fact]
    public void WhenAPointIsUnknown_ThenAddingTheModelFails()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new SunSpecTestChain().AddModel(103, new Dictionary<string, object?> { ["Watts"] = 1 }));
    }

    [Fact]
    public void WhenLoadingThePySunSpecDump_ThenModelsAreEncoded()
    {
        // Act
        var registers = SunSpecFixtures.LoadPySunSpec("device_1547.json", [1, 701], "VL3N").Build();

        // Assert
        Assert.Equal(1, registers[2]);
    }
}
