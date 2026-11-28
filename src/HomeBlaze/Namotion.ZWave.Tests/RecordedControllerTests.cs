using Namotion.Interceptor.Testing;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests;

/// <summary>
/// Replays the startup responses recorded from real controllers. The home ID is replaced, every other byte is as recorded.
/// </summary>
public class RecordedControllerTests
{
    [Fact]
    public async Task WhenAnAeotecZStickGen5Answers_ThenItsRecordedStartupIsParsed()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        stick.SetResponse(FunctionId.GetControllerVersion, Convert.FromHexString("5A2D5761766520342E35340001"));
        stick.SetResponse(FunctionId.GetControllerId, Convert.FromHexString("DEADBEEF01"));
        stick.SetResponse(FunctionId.GetSerialApiCapabilities, Convert.FromHexString(
            "010100860001005A" + "FE81FF884F1F0000FB9F7DA0670000800080860000" + "00E87300000E0000600000"));
        stick.SetResponse(FunctionId.GetSerialApiInitData, Convert.FromHexString(
            "05081D" + "C1EF5E" + new string('0', 26 * 2) + "0500"));
        stick.SetResponse(FunctionId.GetControllerCapabilities, Convert.FromHexString("1C"));

        // Act
        await using var controller = await ZWaveController.ConnectAsync(stick).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal("Z-Wave 4.54", controller.LibraryVersion);
        Assert.Equal(ZWaveLibraryType.StaticController, controller.LibraryType);
        Assert.Equal("1.1", controller.SerialApiVersion);
        Assert.Equal(0x0086, controller.ManufacturerId);
        Assert.Equal(0x0001, controller.ProductType);
        Assert.Equal(0x005A, controller.ProductId);
        Assert.Equal(0xDEADBEEFu, controller.HomeId);
        Assert.Equal(1, controller.OwnNodeId);
        Assert.Equal(
            ZWaveControllerCapabilities.SisPresent | ZWaveControllerCapabilities.WasRealPrimary | ZWaveControllerCapabilities.Suc,
            controller.Capabilities);
        Assert.Equal(new byte[] { 1, 7, 8, 9, 10, 11, 12, 14, 15, 16, 18, 19, 20, 21, 23 }, controller.NodeIds);
        Assert.True(controller.SupportsFunction(0x13));
        Assert.True(controller.SupportsFunction(FunctionId.GetControllerId));
    }
}
