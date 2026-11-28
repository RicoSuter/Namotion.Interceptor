using Namotion.ZWave.Transport;
using Xunit.Abstractions;

namespace Namotion.ZWave.Tests.Hardware;

[Trait("Category", "Hardware")]
public class ZWaveHardwareTests(ITestOutputHelper output)
{
    [ZWaveHardwareFact]
    public async Task WhenConnectedToARealController_ThenItReportsItsNetwork()
    {
        // Arrange
        var port = Environment.GetEnvironmentVariable(ZWaveHardwareFactAttribute.PortVariable)!;

        // Act
        await using var controller = await ZWaveController.ConnectAsync(new SerialTransport(port), loggerFactory: new TestOutputLoggerFactory(output));

        // Assert
        output.WriteLine($"Library: {controller.LibraryVersion} ({controller.LibraryType})");
        output.WriteLine($"Serial API: {controller.SerialApiVersion}, manufacturer 0x{controller.ManufacturerId:X4}, product type 0x{controller.ProductType:X4}, product 0x{controller.ProductId:X4}");
        output.WriteLine($"Home ID: 0x{controller.HomeId:X8}, own node ID: {controller.OwnNodeId}, capabilities: {controller.Capabilities}");
        output.WriteLine($"Nodes: {string.Join(", ", controller.NodeIds)}");

        Assert.NotEqual(0u, controller.HomeId);
        Assert.Contains(controller.OwnNodeId, controller.NodeIds);
    }
}
