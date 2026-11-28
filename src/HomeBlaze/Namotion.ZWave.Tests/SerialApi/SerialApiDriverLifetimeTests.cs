using Namotion.Interceptor.Testing;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests.SerialApi;

public class SerialApiDriverLifetimeTests
{
    [Fact]
    public async Task WhenStartedTwice_ThenTheSecondStartThrows()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(driver.Start);
    }

    [Fact]
    public async Task WhenStartedAfterDisposal_ThenStartThrows()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        var driver = await SerialApiDriverTests.CreateDriverAsync(stick);
        await driver.DisposeAsync();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(driver.Start);
        Assert.Empty(stick.ReceivedFrames);
    }

    [Fact]
    public async Task WhenTheStartupNakCannotBeWritten_ThenCompletionAndRequestsFailWithAConnectionException()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        var driver = await SerialApiDriverTests.CreateDriverAsync(stick);
        stick.FailWrites = true;

        // Act
        driver.Start();

        // Assert
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => driver.Completion.WaitAsync(TestTimeouts.Default));
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None).WaitAsync(TestTimeouts.Default));
        await driver.DisposeAsync().AsTask().WaitAsync(TestTimeouts.Default);
        Assert.True(stick.IsDisposed);
    }

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
    public async Task WhenAnInFlightRequestIsCanceled_ThenItCompletesAtOnceAndTheNextRequestWaitsForTheExchangeToFinish()
    {
        // Arrange
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        stick.SetResponse(FunctionId.GetControllerId, 0xC0, 0xFF, 0xEE, 0x01, 0x01);
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick);
        using var cancellation = new CancellationTokenSource();
        var canceled = driver.SendRequestAsync(FunctionId.GetControllerVersion, default, cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        await cancellation.CancelAsync();
        var canceledAtOnce = canceled.IsCanceled;
        var next = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        stick.AcknowledgesFrames = true;
        stick.SendToHost(FrameHeader.Ack);
        stick.SendFrame(FrameType.Response, FunctionId.GetControllerVersion, 0x00, 0x01);
        await next.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.True(canceledAtOnce);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        // The host acknowledges the canceled request's response before it writes the next request.
        Assert.Equal(
            new[] { "Nak", "Data 15", "Ack", "Data 20", "Ack" },
            stick.ReceivedFrames.Select(frame => frame.Kind == FrameKind.Data ? $"Data {frame.Data.Function:X2}" : frame.Kind.ToString()));
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
    public async Task WhenAWriteFails_ThenTheRequestAndCompletionFailWithAConnectionException()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick);
        stick.FailWrites = true;

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => request.WaitAsync(TestTimeouts.Default));
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => driver.Completion.WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenAWriteDoesNotCompleteWithinTheAcknowledgementTimeout_ThenTheRequestAndCompletionFailWithAConnectionException()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick();
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick, time);
        stick.HangWrites = true;

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        var writeTimer = await time.WaitForTimerAsync(TimeSpan.FromMilliseconds(1600));
        time.Advance(TimeSpan.FromMilliseconds(1599));
        var completedBeforeTimeout = request.IsCompleted;
        time.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(TimeSpan.Zero, writeTimer.StartedAt);
        Assert.False(completedBeforeTimeout);
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => request.WaitAsync(TestTimeouts.Default));
        await Assert.ThrowsAsync<ZWaveConnectionException>(() => driver.Completion.WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenDisposedDuringAWriteThatIgnoresCancellation_ThenTheRequestFailsWithObjectDisposed()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        var driver = await SerialApiDriverTests.StartDriverAsync(stick);
        stick.HangWrites = true;
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.HungWriteCount == 1);

        // Act
        await driver.DisposeAsync().AsTask().WaitAsync(TestTimeouts.Default);

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request.WaitAsync(TestTimeouts.Default));
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

    [Fact]
    public async Task WhenClosingTheStreamFails_ThenDisposingStillFailsPendingRequestsAndCompletes()
    {
        // Arrange
        var stick = new FakeZWaveStick { AcknowledgesFrames = false, ThrowsOnDispose = true };
        var driver = await SerialApiDriverTests.StartDriverAsync(stick);
        var pending = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stick.ReceivedDataFrames.Count == 1);

        // Act
        var exception = await Record.ExceptionAsync(() => driver.DisposeAsync().AsTask().WaitAsync(TestTimeouts.Default));

        // Assert
        Assert.Null(exception);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.True(driver.Completion.IsCompletedSuccessfully);
    }
}
