using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests;

public class ZWaveControllerTests
{
    [Fact]
    public async Task WhenConnected_ThenTheStartupInformationIsRead()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();

        // Act
        await using var controller = await ZWaveController.ConnectAsync(stick);

        // Assert
        Assert.Equal("Z-Wave 4.05", controller.LibraryVersion);
        Assert.Equal(ZWaveLibraryType.StaticController, controller.LibraryType);
        Assert.Equal(0xC0FFEE01u, controller.HomeId);
        Assert.Equal(1, controller.OwnNodeId);
        Assert.Equal("1.2", controller.SerialApiVersion);
        Assert.Equal(0x0086, controller.ManufacturerId);
        Assert.Equal(0x0001, controller.ProductType);
        Assert.Equal(0x005A, controller.ProductId);
        Assert.Equal(
            ZWaveControllerCapabilities.SisPresent | ZWaveControllerCapabilities.WasRealPrimary | ZWaveControllerCapabilities.Suc,
            controller.Capabilities);
        Assert.Equal(new byte[] { 1, 2, 5 }, controller.NodeIds);
        Assert.True(controller.SupportsFunction(FunctionId.GetControllerId));
        Assert.False(controller.SupportsFunction(0x13));
    }

    [Fact]
    public async Task WhenAStartupResponseIsInvalid_ThenConnectFailsAndTheStreamIsClosed()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        stick.SetResponse(FunctionId.GetControllerId, 0xC0);

        // Act & Assert
        await Assert.ThrowsAsync<ZWaveException>(() => ZWaveController.ConnectAsync(stick));
        Assert.True(stick.IsDisposed);
    }

    [Fact]
    public async Task WhenOptionsAreInvalid_ThenConnectThrowsBeforeOpeningTheTransport()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        var options = new ZWaveControllerOptions { MaximumSendAttempts = 0 };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ZWaveController.ConnectAsync(stick, options));
        Assert.Empty(stick.ReceivedFrames);
    }

    [Fact]
    public async Task WhenTheControllerSendsAnUnsolicitedFrame_ThenItIsAcknowledged()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        await using var controller = await ZWaveController.ConnectAsync(stick);
        var acksBefore = stick.ReceivedAckCount;

        // Act
        stick.SendFrame(FrameType.Request, 0x04, 0x00, 0x05, 0x03, 0x20, 0x03, 0xFF);

        // Assert
        await Namotion.Interceptor.Testing.AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedAckCount == acksBefore + 1);
    }

    [Fact]
    public async Task WhenTheStickIsUnplugged_ThenCompletionFaults()
    {
        // Arrange
        var stick = FakeZWaveStick.CreateWithController();
        await using var controller = await ZWaveController.ConnectAsync(stick);

        // Act
        stick.Disconnect();

        // Assert
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => controller.Completion.WaitAsync(Namotion.Interceptor.Testing.TestTimeouts.Default));
    }
}
