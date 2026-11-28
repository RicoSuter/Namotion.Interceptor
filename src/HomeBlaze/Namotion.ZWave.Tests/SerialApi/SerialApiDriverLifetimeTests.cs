using Namotion.Interceptor.Testing;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests.SerialApi;

public class SerialApiDriverLifetimeTests
{
    [Fact]
    public async Task WhenARequestIsCanceledWhileQueued_ThenItIsNeverWritten()
    {
        // Arrange
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        stick.SetResponse(FunctionId.GetControllerVersion, 0x00, 0x01);
        stick.SetResponse(FunctionId.GetControllerCapabilities, 0x00);
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick);
        var first = driver.SendRequestAsync(FunctionId.GetControllerVersion, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        using var cancellation = new CancellationTokenSource();
        var second = driver.SendRequestAsync(FunctionId.GetControllerId, default, cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        stick.SendToHost(FrameHeader.Ack);
        stick.SendFrame(FrameType.Response, FunctionId.GetControllerVersion, 0x00, 0x01);
        await first.WaitAsync(TestTimeouts.Default);
        stick.AcknowledgesFrames = true;
        await driver.SendRequestAsync(FunctionId.GetControllerCapabilities, default, CancellationToken.None).WaitAsync(TestTimeouts.Default);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(
            new[] { FunctionId.GetControllerVersion, FunctionId.GetControllerCapabilities },
            stick.ReceivedDataFrames.Select(frame => frame.Function));
    }

    [Fact]
    public async Task WhenTheConnectionCloses_ThenPendingAndLaterRequestsFailAndCompletionFaults()
    {
        // Arrange
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick);
        var pending = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        stick.Disconnect();

        // Assert
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => pending.WaitAsync(TestTimeouts.Default));
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => driver.Completion.WaitAsync(TestTimeouts.Default));
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheConnectionClosesBeforeARetransmission_ThenThePendingRequestFailsWithoutWaitingForTheDelay()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        stick.EnqueueAcknowledgement(FrameHeader.Nak);
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick);
        var pending = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        stick.Disconnect();

        // Assert
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => pending.WaitAsync(TestTimeouts.Default));
        Assert.Single(stick.ReceivedDataFrames);
    }

    [Fact]
    public async Task WhenDisposed_ThenPendingRequestsFailWithObjectDisposedAndTheStreamIsClosed()
    {
        // Arrange
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        var driver = await SerialApiDriverTests.StartDriverAsync(stick);
        var pending = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        await driver.DisposeAsync();

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.True(stick.IsDisposed);
        Assert.True(driver.Completion.IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => { _ = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None); });
    }

    [Fact]
    public async Task WhenDisposedTwice_ThenTheSecondCallDoesNothing()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        var driver = await SerialApiDriverTests.StartDriverAsync(stick);

        // Act
        await driver.DisposeAsync();
        var exception = await Record.ExceptionAsync(async () => await driver.DisposeAsync());

        // Assert
        Assert.Null(exception);
    }
}
