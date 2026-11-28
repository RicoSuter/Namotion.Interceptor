using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Testing;
using Namotion.ZWave.SerialApi;
using Namotion.ZWave.Tests.Testing;

namespace Namotion.ZWave.Tests.SerialApi;

public class FrameStreamTests
{
    private static readonly TimeSpan FrameDeliveryTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan LateReaderGrace = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task WhenAPartialFrameSwallowsTheFollowingFrames_ThenItIsDroppedAfterTheFrameDeliveryTimeoutAndTheyAreRead()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var logger = new RecordingLogger();
        var stick = new FakeZWaveStick();
        await using var stream = await stick.OpenAsync(CancellationToken.None);
        using var frames = new FrameStream(stream, new ZWaveControllerOptions { TimeProvider = time }, logger);

        // Act
        var first = ReadAsync(frames);
        stick.SendToHost([0x01, 0xFE, FrameHeader.Ack, .. FrameEncoder.Encode(FrameType.Response, 0x15, [0x01])]);
        var timer = await time.WaitForTimerAsync(FrameDeliveryTimeout);
        time.Advance(FrameDeliveryTimeout - Tick);
        var completedBeforeTimeout = first.IsCompleted;
        time.Advance(Tick);
        var acknowledgement = await first.WaitAsync(TestTimeouts.Default);
        var response = await ReadAsync(frames).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(TimeSpan.Zero, timer.StartedAt);
        Assert.False(completedBeforeTimeout);
        Assert.Equal(FrameKind.Ack, acknowledgement?.Kind);
        Assert.Equal(FrameKind.Data, response?.Kind);
        Assert.Equal(0x15, response!.Value.Data.Function);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task WhenFramesArriveAfterAPartialFrameWasDropped_ThenTheyAreReadWithoutAnotherTimer()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick();
        await using var stream = await stick.OpenAsync(CancellationToken.None);
        using var frames = new FrameStream(stream, new ZWaveControllerOptions { TimeProvider = time }, new RecordingLogger());

        // Act
        var first = ReadAsync(frames);
        stick.SendToHost(0x01, 0xFE);
        await time.WaitForTimerAsync(FrameDeliveryTimeout);
        time.Advance(FrameDeliveryTimeout);
        stick.SendToHost(FrameHeader.Ack);
        var acknowledgement = await first.WaitAsync(TestTimeouts.Default);
        var secondRead = ReadAsync(frames);
        stick.SendFrame(FrameType.Response, 0x15, 0x01);
        var response = await secondRead.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(FrameKind.Ack, acknowledgement?.Kind);
        Assert.Equal(0x15, response!.Value.Data.Function);
        Assert.Single(time.Timers);
    }

    [Fact]
    public async Task WhenAPartialFrameCompletesWithinTheFrameDeliveryTimeout_ThenItIsRead()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var logger = new RecordingLogger();
        var stick = new FakeZWaveStick();
        await using var stream = await stick.OpenAsync(CancellationToken.None);
        using var frames = new FrameStream(stream, new ZWaveControllerOptions { TimeProvider = time }, logger);
        var bytes = FrameEncoder.Encode(FrameType.Response, 0x15, [0x01, 0x02]);

        // Act
        var read = ReadAsync(frames);
        stick.SendToHost(bytes[..^1]);
        await time.WaitForTimerAsync(FrameDeliveryTimeout);
        time.Advance(FrameDeliveryTimeout - Tick);
        stick.SendToHost(bytes[^1]);
        var frame = await read.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(FrameKind.Data, frame?.Kind);
        Assert.Equal(new byte[] { 0x01, 0x02 }, frame!.Value.Data.Payload);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task WhenTheReaderResumesAfterTheDeadlineWithTheRestOfTheFrameAlreadyArrived_ThenTheFrameIsRead()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick();
        var bytes = FrameEncoder.Encode(FrameType.Response, 0x15, [0x01, 0x02, 0x03]);
        var secondChunk = bytes[2..5];
        var thirdChunk = bytes[5..];

        // Stalls the reader while it handles the second chunk: the deadline passes and the third chunk arrives meanwhile.
        var logger = new StallingLogger($"Received {Convert.ToHexString(secondChunk)}.", () =>
        {
            time.Advance(FrameDeliveryTimeout);
            stick.SendToHost(thirdChunk);
        });
        await using var stream = await stick.OpenAsync(CancellationToken.None);
        using var frames = new FrameStream(stream, new ZWaveControllerOptions { TimeProvider = time }, logger);

        // Act
        var read = ReadAsync(frames);
        stick.SendToHost(bytes[..2]);
        await time.WaitForTimerAsync(FrameDeliveryTimeout);
        stick.SendToHost(secondChunk);
        var frame = await read.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(FrameKind.Data, frame?.Kind);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, frame!.Value.Data.Payload);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenTheReaderResumesAfterTheDeadline_ThenTheRestOfTheFrameGetsTheGraceToArrive(bool arrivesWithinGrace)
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick();
        var bytes = FrameEncoder.Encode(FrameType.Response, 0x15, [0x01, 0x02, 0x03]);
        var secondChunk = bytes[2..5];
        var logger = new StallingLogger($"Received {Convert.ToHexString(secondChunk)}.", () => time.Advance(FrameDeliveryTimeout));
        await using var stream = await stick.OpenAsync(CancellationToken.None);
        using var frames = new FrameStream(stream, new ZWaveControllerOptions { TimeProvider = time }, logger);

        // Act
        var read = ReadAsync(frames);
        stick.SendToHost(bytes[..2]);
        await time.WaitForTimerAsync(FrameDeliveryTimeout);
        stick.SendToHost(secondChunk);
        var graceTimer = await time.WaitForTimerAsync(LateReaderGrace);
        time.Advance(LateReaderGrace - Tick);
        if (arrivesWithinGrace)
        {
            stick.SendToHost(bytes[5..]);
        }
        else
        {
            time.Advance(Tick);
        }

        var frame = await read.WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(FrameDeliveryTimeout, graceTimer.StartedAt);
        if (arrivesWithinGrace)
        {
            Assert.Equal(FrameKind.Data, frame?.Kind);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
        }
        else
        {
            // The payload byte 0x06 after the dropped start of frame byte is read as an ACK.
            Assert.Equal(FrameKind.Ack, frame?.Kind);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        }
    }

    [Fact]
    public async Task WhenOnlyCompleteFramesArrive_ThenNoTimerIsStarted()
    {
        // Arrange
        var time = new TimerRecordingTimeProvider();
        var stick = new FakeZWaveStick();
        await using var stream = await stick.OpenAsync(CancellationToken.None);
        using var frames = new FrameStream(stream, new ZWaveControllerOptions { TimeProvider = time }, new RecordingLogger());

        // Act
        stick.SendToHost([FrameHeader.Ack, .. FrameEncoder.Encode(FrameType.Response, 0x15, [0x01])]);
        var acknowledgement = await ReadAsync(frames).WaitAsync(TestTimeouts.Default);
        var response = await ReadAsync(frames).WaitAsync(TestTimeouts.Default);

        // Assert
        Assert.Equal(FrameKind.Ack, acknowledgement?.Kind);
        Assert.Equal(FrameKind.Data, response?.Kind);
        Assert.Empty(time.Timers);
    }

    // Runs the action when the given message is logged, on the logging thread.
    private sealed class StallingLogger(string message, Action action) : RecordingLogger, ILogger
    {
        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Log(logLevel, eventId, state, exception, formatter);
            if (formatter(state, exception) == message)
            {
                action();
            }
        }
    }

    // Sonar (S5034) reads two ReadAsync calls in one method as one ValueTask consumed twice.
    private static Task<ParsedFrame?> ReadAsync(FrameStream frames) => frames.ReadAsync(CancellationToken.None).AsTask();
}
