using Microsoft.Extensions.Time.Testing;
using Namotion.Interceptor.Testing;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests.SerialApi;

public class SerialApiDriverRetransmissionTests
{
    [Fact]
    public async Task WhenNoAckArrives_ThenTheFrameIsWrittenThreeTimesAndTheRequestTimesOut()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var stick = new FakeZWaveStick { AcknowledgesFrames = false };
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick, time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AdvanceUntilAsync(time, () => request.IsCompleted);

        // Assert
        await Assert.ThrowsAsync<ZWaveTimeoutException>(() => request);
        Assert.Equal(3, stick.ReceivedDataFrames.Count);
    }

    [Theory]
    [InlineData(FrameHeader.Nak)]
    [InlineData(FrameHeader.Can)]
    public async Task WhenTheFirstAttemptIsRejected_ThenTheFrameIsRetransmittedAndAnswered(byte rejection)
    {
        // Arrange
        var time = new FakeTimeProvider();
        var stick = new FakeZWaveStick();
        stick.EnqueueAcknowledgement(rejection);
        stick.SetResponse(FunctionId.GetControllerId, 0xC0, 0xFF, 0xEE, 0x01, 0x01);
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick, time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AdvanceUntilAsync(time, () => request.IsCompleted);

        // Assert
        var response = await request;
        Assert.Equal(FunctionId.GetControllerId, response.Function);
        Assert.Equal(2, stick.ReceivedDataFrames.Count);
    }

    [Fact]
    public async Task WhenEveryAttemptIsRejected_ThenTheRequestFailsWithAControllerException()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var stick = new FakeZWaveStick();
        stick.EnqueueAcknowledgement(FrameHeader.Nak);
        stick.EnqueueAcknowledgement(FrameHeader.Nak);
        stick.EnqueueAcknowledgement(FrameHeader.Nak);
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick, time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AdvanceUntilAsync(time, () => request.IsCompleted);

        // Assert
        await Assert.ThrowsAsync<ZWaveControllerException>(() => request);
    }

    [Fact]
    public async Task WhenTheResponseDoesNotArrive_ThenTheRequestTimesOutAfterOneWrite()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var stick = new FakeZWaveStick();
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick, time);

        // Act
        var request = driver.SendRequestAsync(FunctionId.GetControllerId, default, CancellationToken.None);
        await AdvanceUntilAsync(time, () => request.IsCompleted);

        // Assert
        await Assert.ThrowsAsync<ZWaveTimeoutException>(() => request);
        Assert.Single(stick.ReceivedDataFrames);
    }

    [Fact]
    public async Task WhenACommandIsAcknowledged_ThenItCompletesWithoutAResponse()
    {
        // Arrange
        var stick = new FakeZWaveStick();
        await using var driver = await SerialApiDriverTests.StartDriverAsync(stick);

        // Act
        await driver.SendCommandAsync(0x08, default, CancellationToken.None).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Single(stick.ReceivedDataFrames);
    }

    // Advances the fake clock in steps while the real clock polls, so that every timer the driver starts fires.
    private static Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition) =>
        AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                if (condition())
                {
                    return true;
                }

                time.Advance(TimeSpan.FromMilliseconds(500));
                return condition();
            },
            pollInterval: TimeSpan.FromMilliseconds(10));
}
