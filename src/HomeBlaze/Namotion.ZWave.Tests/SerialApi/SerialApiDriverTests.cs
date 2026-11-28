using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests.SerialApi;

public class SerialApiDriverTests
{
    [Fact]
    public async Task WhenStarted_ThenANakIsSentFirst()
    {
        // Arrange
        var stick = new FakeZWaveStick();

        // Act
        await using var driver = await StartDriverAsync(stick);

        // Assert
        Assert.Equal(FrameKind.Nak, stick.ReceivedFrames[0].Kind);
    }

    [Fact]
    public async Task WhenRequestIsAcknowledgedAndAnswered_ThenTheResponseIsReturned()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        stick.SetResponse(FunctionId.GetControllerId, 0xC0, 0xFF, 0xEE, 0x01, 0x01);
        await using var driver = await StartDriverAsync(stick);

        // Act
        var response = await driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);

        // Assert
        Assert.Equal(FrameType.Response, response.Type);
        Assert.Equal(new byte[] { 0xC0, 0xFF, 0xEE, 0x01, 0x01 }, response.Payload);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedAckCount == 1);
    }

    [Fact]
    public async Task WhenAStaleResponseArrivesBeforeTheAck_ThenItIsIgnoredAndTheResponseRightBehindTheAckIsReturned()
    {
        // Arrange
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        await using var driver = await StartDriverAsync(stick);
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        stick.SendToHost([
            .. FrameEncoder.Encode(FrameType.Response, FunctionId.GetControllerId, [0x11]),
            FrameHeader.Ack,
            .. FrameEncoder.Encode(FrameType.Response, FunctionId.GetControllerId, [0x22])]);
        var response = await request.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(new byte[] { 0x22 }, response.Payload);
    }

    [Fact]
    public async Task WhenControllerSendsARequestFrame_ThenItIsAcknowledgedAndHandedToTheUnsolicitedFrames()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        await using var driver = await StartDriverAsync(stick);

        // Act
        stick.SendFrame(FrameType.Request, 0x04, 0x00, 0x05, 0x03, 0x20, 0x03, 0xFF);
        var frame = await driver.UnsolicitedFrames.ReadAsync().AsTask().WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(0x04, frame.Function);
        Assert.Equal(new byte[] { 0x00, 0x05, 0x03, 0x20, 0x03, 0xFF }, frame.Payload);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedAckCount == 1);
    }

    [Fact]
    public async Task WhenControllerSendsABadChecksum_ThenANakIsSent()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        await using var driver = await StartDriverAsync(stick);
        var naksAfterStart = stick.ReceivedNakCount;

        // Act
        stick.SendToHost(0x01, 0x03, 0x00, 0x15, 0x00);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedNakCount == naksAfterStart + 1);
    }

    /// <summary>
    /// Creates and starts a driver on the stick, and waits until the startup NAK was written.
    /// </summary>
    internal static async Task<SerialApiDriver> StartDriverAsync(FakeZWaveStick stick, TimeProvider? timeProvider = null)
    {
        var driver = await CreateDriverAsync(stick, timeProvider);
        driver.Start();
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedNakCount == 1);
        return driver;
    }

    internal static async Task<SerialApiDriver> CreateDriverAsync(FakeZWaveStick stick, TimeProvider? timeProvider = null)
    {
        var stream = await stick.OpenAsync(CancellationToken.None);
        var options = new ZWaveControllerOptions { TimeProvider = timeProvider ?? new FakeTimeProvider() };
        return new SerialApiDriver(stream, options, NullLogger.Instance);
    }
}
