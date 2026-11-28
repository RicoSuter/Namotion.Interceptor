using Microsoft.Extensions.Logging;
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
        await using var driver = await stick.StartDriverAsync();

        // Assert
        Assert.Equal(FrameKind.Nak, stick.ReceivedFrames[0].Kind);
    }

    [Fact]
    public async Task WhenRequestIsAcknowledgedAndAnswered_ThenTheResponseIsReturned()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        stick.SetResponse(FunctionId.GetControllerId, 0xC0, 0xFF, 0xEE, 0x01, 0x01);
        await using var driver = await stick.StartDriverAsync();

        // Act
        var response = await driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None).WaitAsync(TestTimeouts.Default);

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
        await using var driver = await stick.StartDriverAsync();
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
    public async Task WhenAResponseToAnotherFunctionArrives_ThenItIsIgnoredAndTheMatchingResponseIsReturned()
    {
        // Arrange
        var logger = new RecordingLogger();
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        await using var driver = await stick.StartDriverAsync(logger: logger);
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        stick.SendToHost([
            FrameHeader.Ack,
            .. FrameEncoder.Encode(FrameType.Response, FunctionId.GetControllerVersion, [0x11]),
            .. FrameEncoder.Encode(FrameType.Response, FunctionId.GetControllerId, [0x22])]);
        var response = await request.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(new byte[] { 0x22 }, response.Payload);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message == "Received an unexpected response to function 0x15.");
    }

    [Fact]
    public async Task WhenAnAckArrivesWithNothingPending_ThenItIsLoggedAndTheNextRequestIsAnswered()
    {
        // Arrange
        var logger = new RecordingLogger();
        var stick = new FakeZWaveStick();
        stick.SetResponse(FunctionId.GetControllerId, 0x22);
        await using var driver = await stick.StartDriverAsync(logger: logger);

        // Act
        stick.SendToHost(FrameHeader.Ack);
        await AsyncTestHelpers.WaitUntilAsync(() => logger.Entries.Any(entry => entry.Message == "Received an unexpected Ack."));
        var response = await driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(new byte[] { 0x22 }, response.Payload);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message == "Received an unexpected Ack.");
    }

    [Fact]
    public async Task WhenASecondAckArrivesForTheSameAttempt_ThenItIsLoggedAsUnexpected()
    {
        // Arrange
        var logger = new RecordingLogger();
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        await using var driver = await stick.StartDriverAsync(logger: logger);
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        stick.SendToHost([
            FrameHeader.Ack,
            FrameHeader.Ack,
            .. FrameEncoder.Encode(FrameType.Response, FunctionId.GetControllerId, [0x22])]);
        var response = await request.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(new byte[] { 0x22 }, response.Payload);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message == "Received an unexpected Ack.");
    }

    [Fact]
    public async Task WhenControllerSendsARequestFrame_ThenItIsAcknowledgedAndHandedToTheUnsolicitedFrames()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        await using var driver = await stick.StartDriverAsync();

        // Act
        stick.SendFrame(FrameType.Request, 0x04, 0x00, 0x05, 0x03, 0x20, 0x03, 0xFF);
        var frame = await driver.UnsolicitedFrames.ReadAsync().AsTask().WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(0x04, frame.Function);
        Assert.Equal(new byte[] { 0x00, 0x05, 0x03, 0x20, 0x03, 0xFF }, frame.Payload);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedAckCount == 1);
    }

    [Fact]
    public async Task WhenACommandIsAcknowledged_ThenItCompletesWithoutAResponse()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        await using var driver = await stick.StartDriverAsync();

        // Act
        await driver.SendCommandAsync(0x08, default, CancellationToken.None).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Single(stick.ReceivedDataFrames);
    }

    [Fact]
    public async Task WhenControllerSendsABadChecksum_ThenANakIsSent()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        await using var driver = await stick.StartDriverAsync();
        var naksAfterStart = stick.ReceivedNakCount;

        // Act
        stick.SendToHost(0x01, 0x03, 0x00, 0x15, 0x00);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedNakCount == naksAfterStart + 1);
    }
}
