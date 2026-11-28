using Namotion.Interceptor.Testing;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests.SerialApi;

// The clock is advanced only once the driver has started the timer the step expects, and never across the
// acknowledgement timeout of an attempt whose ACK the stick sends, so no step races the driver.
public class SerialApiDriverRetransmissionTests
{
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan FirstRetransmissionDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SecondRetransmissionDelay = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task WhenNoAttemptIsAnswered_ThenTheFrameIsWrittenThreeTimesWithTheSpecifiedDelaysAndTheConnectionFails()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        await using var driver = await stick.StartDriverAsync(time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);

        var firstAcknowledgementTimer = await time.WaitForTimerAsync(AcknowledgementTimeout);
        time.Advance(AcknowledgementTimeout - Tick);
        var framesBeforeFirstTimeout = stick.ReceivedDataFrames.Count;
        var delayStartedBeforeFirstTimeout = time.Timers.Any(timer => timer.DueTime == FirstRetransmissionDelay);
        time.Advance(Tick);

        var firstDelay = await time.WaitForTimerAsync(FirstRetransmissionDelay);
        time.Advance(FirstRetransmissionDelay - Tick);
        var framesBeforeFirstRetransmission = stick.ReceivedDataFrames.Count;
        time.Advance(Tick);

        var secondAcknowledgementTimer = await time.WaitForTimerAsync(AcknowledgementTimeout, occurrence: 2);
        time.Advance(AcknowledgementTimeout);

        var secondDelay = await time.WaitForTimerAsync(SecondRetransmissionDelay);
        time.Advance(SecondRetransmissionDelay - Tick);
        var framesBeforeSecondRetransmission = stick.ReceivedDataFrames.Count;
        time.Advance(Tick);

        var thirdAcknowledgementTimer = await time.WaitForTimerAsync(AcknowledgementTimeout, occurrence: 3);
        time.Advance(AcknowledgementTimeout);
        var exception = await Record.ExceptionAsync(() => request.WaitAsync(TestTimeouts.Default));

        // Assert
        Assert.Equal(TimeSpan.Zero, firstAcknowledgementTimer.StartedAt);
        Assert.Equal(1, framesBeforeFirstTimeout);
        Assert.False(delayStartedBeforeFirstTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1600), firstDelay.StartedAt);
        Assert.Equal(1, framesBeforeFirstRetransmission);
        Assert.Equal(TimeSpan.FromMilliseconds(1700), secondAcknowledgementTimer.StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(3300), secondDelay.StartedAt);
        Assert.Equal(2, framesBeforeSecondRetransmission);
        Assert.Equal(TimeSpan.FromMilliseconds(4400), thirdAcknowledgementTimer.StartedAt);
        var failure = Assert.IsType<ZWaveConnectionException>(exception);
        Assert.Contains("stopped responding", failure.Message);
        Assert.Same(failure, await Record.ExceptionAsync(() => driver.Completion.WaitAsync(TestTimeouts.Default)));
        Assert.Equal(3, stick.ReceivedDataFrames.Count);
    }

    [Fact]
    public async Task WhenAnEarlierAttemptWasRejectedAndTheLastTimesOut_ThenTheRequestTimesOutAndTheConnectionStaysUp()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        stick.EnqueueAcknowledgement(FrameHeader.Nak);
        await using var driver = await stick.StartDriverAsync(time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await time.WaitForTimerAsync(FirstRetransmissionDelay);
        time.Advance(FirstRetransmissionDelay);
        await time.WaitForTimerAsync(AcknowledgementTimeout, occurrence: 2);
        time.Advance(AcknowledgementTimeout);
        await time.WaitForTimerAsync(SecondRetransmissionDelay);
        time.Advance(SecondRetransmissionDelay);
        await time.WaitForTimerAsync(AcknowledgementTimeout, occurrence: 3);
        time.Advance(AcknowledgementTimeout);
        var exception = await Record.ExceptionAsync(() => request.WaitAsync(TestTimeouts.Default));

        // Assert
        Assert.IsType<ZWaveTimeoutException>(exception);
        Assert.False(driver.Completion.IsCompleted);
        Assert.Equal(3, stick.ReceivedDataFrames.Count);
    }

    [Theory]
    [InlineData(FrameHeader.Nak)]
    [InlineData(FrameHeader.Can)]
    public async Task WhenTheFirstAttemptIsRejected_ThenTheFrameIsRetransmittedAfterTheFirstDelayAndAnswered(byte rejection)
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick();
        stick.EnqueueAcknowledgement(rejection);
        stick.SetResponse(FunctionId.GetControllerId, 0xC0, 0xFF, 0xEE, 0x01, 0x01);
        await using var driver = await stick.StartDriverAsync(time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        var delay = await time.WaitForTimerAsync(FirstRetransmissionDelay);
        time.Advance(FirstRetransmissionDelay - Tick);
        var framesBeforeRetransmission = stick.ReceivedDataFrames.Count;
        time.Advance(Tick);
        var response = await request.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(TimeSpan.Zero, delay.StartedAt);
        Assert.Equal(1, framesBeforeRetransmission);
        Assert.Equal(FunctionId.GetControllerId, response.Function);
        Assert.Equal(2, stick.ReceivedDataFrames.Count);
        Assert.Equal(FirstRetransmissionDelay, time.Elapsed);
    }

    [Fact]
    public async Task WhenEveryAttemptIsRejected_ThenTheRequestFailsWithAControllerExceptionAfterBothDelays()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick();
        stick.EnqueueAcknowledgement(FrameHeader.Nak);
        stick.EnqueueAcknowledgement(FrameHeader.Nak);
        stick.EnqueueAcknowledgement(FrameHeader.Nak);
        await using var driver = await stick.StartDriverAsync(time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        var firstDelay = await time.WaitForTimerAsync(FirstRetransmissionDelay);
        time.Advance(FirstRetransmissionDelay);
        var secondDelay = await time.WaitForTimerAsync(SecondRetransmissionDelay);
        time.Advance(SecondRetransmissionDelay);
        var exception = await Record.ExceptionAsync(() => request.WaitAsync(TestTimeouts.Default));

        // Assert
        Assert.Equal(TimeSpan.Zero, firstDelay.StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(100), secondDelay.StartedAt);
        Assert.IsType<ZWaveControllerException>(exception);
        Assert.Equal(3, stick.ReceivedDataFrames.Count);
    }

    [Fact]
    public async Task WhenTheResponseDoesNotArrive_ThenTheRequestTimesOutAfterTheResponseTimeoutAndOneWrite()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick();
        await using var driver = await stick.StartDriverAsync(time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);

        // The response timer starts only once the ACK was handled, so the clock never races the ACK.
        var responseTimer = await time.WaitForTimerAsync(ResponseTimeout);
        time.Advance(ResponseTimeout - Tick);
        var completedBeforeTimeout = request.IsCompleted;
        time.Advance(Tick);
        var exception = await Record.ExceptionAsync(() => request.WaitAsync(TestTimeouts.Default));

        // Assert
        Assert.Equal(TimeSpan.Zero, responseTimer.StartedAt);
        Assert.False(completedBeforeTimeout);
        Assert.IsType<ZWaveTimeoutException>(exception);
        Assert.Single(stick.ReceivedDataFrames);
    }
}
