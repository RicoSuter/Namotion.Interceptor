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

    private static GameVolumeTable CreateTable(params (string Listener, string Speaker, float Volume)[] entries)
    {
        var rows = new Dictionary<string, List<GameVolumeEntry>>();
        foreach (var (listener, speaker, volume) in entries)
        {
            if (!rows.TryGetValue(listener, out var row))
            {
                row = [];
                rows[listener] = row;
            }

            row.Add(new GameVolumeEntry(speaker, volume));
        }

        return GameVolumeTable.Create(rows);
    }

    private void Speak(string playerId, short sample, int pageId = 1)
    {
        _mixer.SetMicrophonePage(playerId, pageId);
        _mixer.Receive(playerId, pageId, VoiceFrames.Create(sample));
    }

    [Fact]
    public void WhenTwoSpeakersAreAudible_ThenTheListenerGetsTheirSumScaledByVolume()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("first", 1000);
        Speak("second", 2000);
        var table = CreateTable(("listener", "first", 1f), ("listener", "second", 0.5f));

        // Act
        _mixer.Mix(table);

        // Assert
        var frame = Assert.Single(_sink.Frames);
        Assert.Equal(GameVoiceSettings.FrameBytes, frame.Length);
        Assert.Equal(2000, VoiceFrames.ReadSample(frame, 0));
        Assert.Equal(2000, VoiceFrames.ReadSample(frame, GameVoiceSettings.SamplesPerFrame - 1));
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
        Assert.Equal(expected, VoiceFrames.ReadSample(Assert.Single(_sink.Frames), 0));
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
        Assert.Equal(2000, VoiceFrames.ReadSample(Assert.Single(_sink.Frames), 0));
    }

    [Fact]
    public void WhenTheListenerHearsNobody_ThenItsSinkGetsNothing()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("speaker", 1000);

        // Act
        _mixer.Mix(CreateTable(("other", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheSpeakerHasNoMicrophonePage_ThenItsFramesAreDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.Receive("speaker", 1, VoiceFrames.Create(1000));

        // Act
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheMicrophonePageIsCleared_ThenTheQueuedFramesAreDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        Speak("speaker", 1000);

        // Act
        _mixer.SetMicrophonePage("speaker", null);
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenAFrameHasTheWrongSize_ThenItIsDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("speaker", 1);
        _mixer.Receive("speaker", 1, new byte[GameVoiceSettings.FrameBytes - 1]);

        // Act
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenFramesComeFasterThanTheTolerance_ThenTheExcessIsDropped()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("speaker", 1);
        var table = CreateTable(("listener", "speaker", 1f));

        // The clock stands still, so the bucket holds only its burst and refuses the last frame.
        for (short sample = 1; sample <= GameVoiceSettings.ServerQueueMaximum + 1; sample++)
        {
            _mixer.Receive("speaker", 1, VoiceFrames.Create(sample));
        }

        // Act
        _mixer.Mix(table);
        _mixer.Mix(table);

        // Assert
        // The queue keeps the newest two frames it accepted: its start size of one plus one growth step.
        var samples = _sink.Frames.Select(frame => (int)VoiceFrames.ReadSample(frame, 0));
        Assert.Equal(new[] { GameVoiceSettings.ServerQueueMaximum - 1, GameVoiceSettings.ServerQueueMaximum }, samples);
    }

    [Fact]
    public void WhenAnotherPageBecomesTheMicrophonePage_ThenOnlyItsFramesCount()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("speaker", 1);
        _mixer.SetMicrophonePage("speaker", 2);
        _mixer.Receive("speaker", 1, VoiceFrames.Create(1000));
        _mixer.Receive("speaker", 2, VoiceFrames.Create(2000));

        // Act
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Equal(2000, VoiceFrames.ReadSample(Assert.Single(_sink.Frames), 0));
    }

    [Fact]
    public void WhenASinkThrows_ThenTheOtherSinksStillGetTheFrameAndTheNextMixRuns()
    {
        // Arrange
        using var failing = _mixer.RegisterSink("listener", new ThrowingVoiceSink());
        using var registration = _mixer.RegisterSink("listener", _sink);
        var table = CreateTable(("listener", "speaker", 1f));
        Speak("speaker", 1000);

        // Act
        var exception = Record.Exception(() => _mixer.Mix(table));
        Speak("speaker", 1000);
        _mixer.Mix(table);

        // Assert
        Assert.Null(exception);
        Assert.Equal(2, _sink.Count);
    }

    [Fact]
    public void WhenNoSpeakerHasAFrame_ThenNothingIsSent()
    {
        // Arrange
        using var registration = _mixer.RegisterSink("listener", _sink);
        _mixer.SetMicrophonePage("speaker", 1);

        // Act
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public void WhenTheSinkIsUnregistered_ThenItGetsNothing()
    {
        // Arrange
        var registration = _mixer.RegisterSink("listener", _sink);
        Speak("speaker", 1000);

        // Act
        registration.Dispose();
        _mixer.Mix(CreateTable(("listener", "speaker", 1f)));

        // Assert
        Assert.Empty(_sink.Frames);
    }

    private sealed class ThrowingVoiceSink : IGameVoiceSink
    {
        public void Send(byte[] frame) => throw new InvalidOperationException("The page is gone.");
    }
}
