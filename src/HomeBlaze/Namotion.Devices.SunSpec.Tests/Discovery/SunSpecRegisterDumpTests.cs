using Namotion.Devices.SunSpec.Discovery;
using Namotion.Devices.SunSpec.Tests.Testing;

namespace Namotion.Devices.SunSpec.Tests.Discovery;

public class SunSpecRegisterDumpTests
{
    [Fact]
    public async Task WhenADumpIsReplayed_ThenTheRegistersAreIdentical()
    {
        // Arrange
        var original = new SunSpecTestChain(50000).AddModel(1, length: 65).AddModel(103, new Dictionary<string, object?> { ["W"] = 1500 });
        var chain = await InMemoryRegisters.ReadAsync(original);

        // Act
        var dump = SunSpecRegisterDump.Write(1, chain!);

        // Assert
        Assert.Equal(original.Build(), SunSpecTestChain.FromDump(dump).Build());
        Assert.Contains("\"unitId\":1", dump);
    }
}
