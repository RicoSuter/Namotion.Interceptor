using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HomeBlaze.Game.Tests.Voice;

public class GameVoiceMixerTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingVoiceSink _sink = new();
    private readonly GameVoiceMixer _mixer;

    public GameVoiceMixerTests()
    {
        _mixer = new GameVoiceMixer(_timeProvider, NullLogger.Instance);
    }

    private static GameVolumeTable CreateTable(params (string Listener, string Talker, float Volume)[] entries)
    {
        var rows = new Dictionary<string, List<GameVolumeEntry>>();
        foreach (var (listener, talker, volume) in entries)
        {
            if (!rows.TryGetValue(listener, out var row))
            {
                row = [];
                rows[listener] = row;
            }

            row.Add(new GameVolumeEntry(talker, volume));
        }

        return GameVolumeTable.Create(rows);
    }

    private void Speak(string playerId, short sample, int pageId = 1)
    {
        _mixer.SetMicrophonePage(playerId, pageId);
        _mixer.Receive(playerId, pageId, VoiceFrames.Create(sample));
    }

    [Fact]
    public void WhenTwoTalkersAreAudible_ThenTheListenerGetsTheirSumScaledByVolume()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        var table = CreateTable(("listener", "first", 1f), ("listener", "second", 0.5f));

        // The first frame of a new pair glides up from 0.
        Speak("first", 1000);
        Speak("second", 2000);
        _mixer.Mix(table);
        Speak("first", 1000);
        Speak("second", 2000);

        // Act
        _mixer.Mix(table);

        // Assert
        var frame = _sink.Frames[^1];
        Assert.Equal(GameVoiceSettings.FrameBytes, frame.Length);
        Assert.Equal(2000, VoiceFrames.ReadSample(frame, 0));
        Assert.Equal(2000, VoiceFrames.ReadLastSample(frame));
    }

    [Theory]
    [InlineData(30000, 32767)]
    [InlineData(-30000, -32768)]
    public void WhenTheSumLeavesThe16BitRange_ThenItIsClamped(int sample, int expected)
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("first", (short)sample);
        Speak("second", (short)sample);

        // Act
        _mixer.Mix(CreateTable(("listener", "first", 1f), ("listener", "second", 1f)));

        // Assert
        Assert.Equal(expected, VoiceFrames.ReadLastSample(Assert.Single(_sink.Frames)));
    }

    [Fact]
    public void WhenTheListenerSpeaks_ThenItDoesNotHearItself()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("listener", 1000);
        Speak("other", 2000);

        // Act
        _mixer.Mix(CreateTable(("listener", "listener", 1f), ("listener", "other", 1f)));

        // Assert
        Assert.Equal(2000, VoiceFrames.ReadLastSample(Assert.Single(_sink.Frames)));
    }

    [Fact]
    public void WhenTheListenerHearsNobody_ThenItsSinkGetsNothing()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("talker", 1000);

        // Act
        _mixer.Mix(CreateTable(("other", "talker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheTalkerHasNoMicrophonePage_ThenItsFramesAreDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.Receive("talker", 1, VoiceFrames.Create(1000));

        // Act
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheMicrophonePageIsCleared_ThenTheQueuedFramesAreDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("talker", 1000);

        // Act
        _mixer.SetMicrophonePage("talker", null);
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenAFrameHasTheWrongSize_ThenItIsDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("talker", 1);
        _mixer.Receive("talker", 1, new byte[GameVoiceSettings.FrameBytes - 1]);

        // Act
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenFramesComeFasterThanTheTolerance_ThenTheExcessIsDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("talker", 1);
        var table = CreateTable(("listener", "talker", 1f));

        // The clock stands still, so the bucket holds only its burst and refuses the last frame.
        for (short sample = 1; sample <= GameVoiceSettings.ServerQueueMaximum + 1; sample++)
        {
            _mixer.Receive("talker", 1, VoiceFrames.Create(sample));
        }

        // Act
        for (var index = 0; index <= GameVoiceSettings.ServerQueueMaximum; index++)
        {
            _mixer.Mix(table);
        }

        // Assert
        // The queue keeps every frame it accepted, and the refused one never plays.
        var samples = _sink.Frames.Select(frame => (int)VoiceFrames.ReadLastSample(frame));
        Assert.Equal(Enumerable.Range(1, GameVoiceSettings.ServerQueueMaximum), samples);
    }

    [Fact]
    public void WhenFramesComeAtTheFrameRate_ThenNoneAreDropped()
    {
        // Arrange
        const int frameCount = 50;
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("talker", 1);
        var table = CreateTable(("listener", "talker", 1f));

        // Act
        for (var index = 0; index < frameCount; index++)
        {
            _timeProvider.Advance(GameVoiceSettings.FrameDuration);
            _mixer.Receive("talker", 1, VoiceFrames.Create(1000));
            _mixer.Mix(table);
        }

        // Assert
        Assert.Equal(frameCount, _sink.Count);
    }

    [Fact]
    public void WhenFramesComeAtTwiceTheFrameRate_ThenSomeAreRefused()
    {
        // Arrange
        const int frameCount = 100;
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("talker", 1);
        var table = CreateTable(("listener", "talker", 1f));

        // Act
        for (var index = 0; index < frameCount; index++)
        {
            _timeProvider.Advance(GameVoiceSettings.FrameDuration / 2);
            _mixer.Receive("talker", 1, VoiceFrames.Create(1000));
            _mixer.Mix(table);
        }

        // Assert
        // Mixing after every frame keeps the queue from dropping, so only the rate limit refuses frames.
        Assert.InRange(_sink.Count, frameCount / 2, frameCount - 1);
    }

    [Fact]
    public void WhenAnotherPageBecomesTheMicrophonePage_ThenOnlyItsFramesCount()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("talker", 1);
        _mixer.SetMicrophonePage("talker", 2);
        _mixer.Receive("talker", 1, VoiceFrames.Create(1000));
        _mixer.Receive("talker", 2, VoiceFrames.Create(2000));

        // Act
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));

        // Assert
        Assert.Equal(2000, VoiceFrames.ReadLastSample(Assert.Single(_sink.Frames)));
    }

    [Fact]
    public void WhenAnotherPageBecomesTheMicrophonePage_ThenTheFramesQueuedBeforeAreDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("talker", 1000, pageId: 1);

        // Act
        _mixer.SetMicrophonePage("talker", 2);
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenASinkThrows_ThenTheOtherSinksStillGetTheFrameAndTheNextMixRuns()
    {
        // Arrange
        using var failing = _mixer.RegisterSink("listener", new ThrowingVoiceSink());
        using var registration = _mixer.RegisterSink("listener", _sink);
        var table = CreateTable(("listener", "talker", 1f));
        Speak("talker", 1000);

        // Act
        var exception = Record.Exception(() => _mixer.Mix(table));
        Speak("talker", 1000);
        _mixer.Mix(table);

        // Assert
        Assert.Null(exception);
        Assert.Equal(2, _sink.Count);
    }

    [Fact]
    public void WhenNoTalkerHasAFrame_ThenNothingIsSent()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("talker", 1);

        // Act
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheSinkIsUnregistered_ThenItGetsNothing()
    {
        // Arrange
        var registration = _mixer.RegisterSink("listener", _sink);
        Speak("talker", 1000);

        // Act
        registration.Dispose();
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenOneOfTwoRegistrationsOfASinkIsDisposed_ThenTheOtherStillSends()
    {
        // Arrange
        var first = _mixer.RegisterSink("listener", _sink);
        using var second = _mixer.RegisterSink("listener", _sink);
        Speak("talker", 1000);

        // Act
        first.Dispose();
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));

        // Assert
        Assert.Equal(1, _sink.Count);
    }

    [Fact]
    public void WhenAPairIsNew_ThenItsGainGlidesUpFromZeroAcrossTheFrame()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("talker", 10000);

        // Act
        _mixer.Mix(CreateTable(("listener", "talker", 0.5f)));

        // Assert
        var frame = Assert.Single(_sink.Frames);
        Assert.InRange(VoiceFrames.ReadSample(frame, 0), 0, 50);
        Assert.InRange(VoiceFrames.ReadSample(frame, GameVoiceSettings.SamplesPerFrame / 2), 2400, 2600);
        Assert.Equal(5000, VoiceFrames.ReadLastSample(frame));
    }

    [Fact]
    public void WhenTheVolumeChangesBetweenTicks_ThenTheGainGlidesFromThePreviousToTheNewVolume()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("talker", 10000);
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));
        Speak("talker", 10000);

        // Act
        _mixer.Mix(CreateTable(("listener", "talker", 0.5f)));

        // Assert
        var frame = _sink.Frames[^1];
        Assert.InRange(VoiceFrames.ReadSample(frame, 0), 9950, 10000);
        Assert.Equal(5000, VoiceFrames.ReadLastSample(frame));
        var samples = Enumerable.Range(0, GameVoiceSettings.SamplesPerFrame).Select(index => VoiceFrames.ReadSample(frame, index)).ToList();
        Assert.All(samples.Zip(samples.Skip(1), (previous, next) => previous - next), step => Assert.InRange(step, 0, 30));
    }

    [Fact]
    public void WhenAPairLeavesTheTable_ThenItGlidesDownToZeroOverOneFrameAndThenStops()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("talker", 10000);
        _mixer.Mix(CreateTable(("listener", "talker", 1f)));
        var empty = CreateTable();

        // Act
        Speak("talker", 10000);
        _mixer.Mix(empty);
        Speak("talker", 10000);
        _mixer.Mix(empty);

        // Assert
        Assert.Equal(2, _sink.Count);
        var frame = _sink.Frames[^1];
        Assert.InRange(VoiceFrames.ReadSample(frame, 0), 9950, 10000);
        Assert.Equal(0, VoiceFrames.ReadLastSample(frame));
    }

    [Fact]
    public void WhenNoTalkerHadAFrameInTheTickBefore_ThenThePairGlidesUpFromZeroAgain()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        var table = CreateTable(("listener", "talker", 1f));
        Speak("talker", 10000);
        _mixer.Mix(table);
        _mixer.Mix(table);

        // The queue ran dry while playing, so it waits for one more frame before it plays again.
        Speak("talker", 10000);
        Speak("talker", 10000);

        // Act
        _mixer.Mix(table);

        // Assert
        var frame = _sink.Frames[^1];
        Assert.InRange(VoiceFrames.ReadSample(frame, 0), 0, 50);
        Assert.Equal(10000, VoiceFrames.ReadLastSample(frame));
    }

    private sealed class ThrowingVoiceSink : IGameVoiceSink
    {
        public void Send(byte[] frame) => throw new InvalidOperationException("The page is gone.");
    }
}
