using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests;

public class GameVoiceTests
{
    private readonly GameTestContext _context = new();

    private static GameCaller Caller(GamePlayer player) => new(player);

    // One tick of the mixer with the table as it is now.
    private void Mix()
    {
        _context.Game.UpdateVolumeTable();
        _context.Game.VoiceMixer.Mix(_context.Game.VolumeTable);
    }

    private void Disconnect(string clientId, bool isNamed)
    {
        _context.Clients.Set(clientId, isNamed, isConnected: false);
        _context.Game.ReconcilePlayers();
    }

    [Fact]
    public void WhenThePlayerTurnsTheMicrophoneOn_ThenItIsOnAndItsFramesReachTheListener()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);

        // Act
        var isAccepted = _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.True(isAccepted);
        Assert.True(speaker.IsMicrophoneOn);
        Assert.Equal(750, VoiceFrames.ReadSample(Assert.Single(sink.Frames), 0));
    }

    [Fact]
    public void WhenASecondPageTurnsTheMicrophoneOn_ThenOnlyItsFramesCount()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);

        // Act
        _context.Game.SetMicrophone(Caller(speaker), pageId: 2, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        _context.Game.VoiceMixer.Receive("tab-1", 2, VoiceFrames.Create(2000));
        Mix();

        // Assert
        Assert.Equal(1500, VoiceFrames.ReadSample(Assert.Single(sink.Frames), 0));
    }

    [Fact]
    public void WhenAnOlderPageTurnsTheMicrophoneOff_ThenTheLatestPageKeepsIt()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 2, isOn: true);

        // Act
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: false);

        // Assert
        Assert.True(speaker.IsMicrophoneOn);
        Assert.Equal((false, false), _context.Game.GetVoicePages(speaker, 1));
        Assert.Equal((true, false), _context.Game.GetVoicePages(speaker, 2));
    }

    [Fact]
    public void WhenTwoPagesTurnTheSpeakerOn_ThenAudioStaysOnUntilBothTurnItOff()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.SetAudio(Caller(player), pageId: 1, isOn: true);
        _context.Game.SetAudio(Caller(player), pageId: 2, isOn: true);

        // Act
        _context.Game.SetAudio(Caller(player), pageId: 1, isOn: false);
        var isOnWithOnePage = player.IsAudioOn;
        _context.Game.SetAudio(Caller(player), pageId: 2, isOn: false);

        // Assert
        Assert.True(isOnWithOnePage);
        Assert.False(player.IsAudioOn);
    }

    [Fact]
    public void WhenTheCallerPlaysAnotherGame_ThenTheTogglesAreRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var stranger = new GameTestContext().AddConnectedPlayer("tab-1");

        // Act
        var isMicrophoneAccepted = _context.Game.SetMicrophone(Caller(stranger), pageId: 1, isOn: true);
        var isAudioAccepted = _context.Game.SetAudio(Caller(stranger), pageId: 1, isOn: true);

        // Assert
        Assert.False(isMicrophoneAccepted);
        Assert.False(isAudioAccepted);
        Assert.False(player.IsMicrophoneOn || player.IsAudioOn);
        Assert.False(stranger.IsMicrophoneOn || stranger.IsAudioOn);
    }

    [Fact]
    public void WhenADisconnectedPlayerTurnsTheMicrophoneOn_ThenItIsRefusedButTurningOffWorks()
    {
        // Arrange
        var kitchen = _context.AddConnectedPlayer("kitchen", isNamed: true);
        Disconnect("kitchen", isNamed: true);

        // Act
        var isTurnedOn = _context.Game.SetMicrophone(Caller(kitchen), pageId: 1, isOn: true);
        var isTurnedOff = _context.Game.SetMicrophone(Caller(kitchen), pageId: 1, isOn: false);

        // Assert
        Assert.False(isTurnedOn);
        Assert.True(isTurnedOff);
        Assert.False(kitchen.IsMicrophoneOn);
    }

    [Fact]
    public void WhenTheClientDisconnects_ThenTheTogglesAreResetAndQueuedFramesDropped()
    {
        // Arrange
        var kitchen = _context.AddConnectedPlayer("kitchen", isNamed: true);
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(kitchen), pageId: 1, isOn: true);
        _context.Game.SetAudio(Caller(kitchen), pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("kitchen", 1, VoiceFrames.Create(1000));

        // Act
        Disconnect("kitchen", isNamed: true);
        Mix();

        // Assert
        Assert.False(kitchen.IsMicrophoneOn);
        Assert.False(kitchen.IsAudioOn);
        Assert.Equal((false, false), _context.Game.GetVoicePages(kitchen, 1));
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void WhenAnAnonymousPlayerLeaves_ThenItsQueuedAndLaterFramesAreDropped()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.SetAudio(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));

        // Act
        Disconnect("tab-1", isNamed: false);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.False(_context.Game.Players.ContainsKey("tab-1"));
        Assert.False(speaker.IsMicrophoneOn);
        Assert.False(speaker.IsAudioOn);
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void WhenADisconnectedPlayerTurnsTheSpeakerOn_ThenItIsRefusedButTurningOffWorks()
    {
        // Arrange
        var kitchen = _context.AddConnectedPlayer("kitchen", isNamed: true);
        Disconnect("kitchen", isNamed: true);

        // Act
        var isTurnedOn = _context.Game.SetAudio(Caller(kitchen), pageId: 1, isOn: true);
        var isTurnedOff = _context.Game.SetAudio(Caller(kitchen), pageId: 1, isOn: false);

        // Assert
        Assert.False(isTurnedOn);
        Assert.True(isTurnedOff);
        Assert.False(kitchen.IsAudioOn);
    }

    [Fact]
    public void WhenPlayersAreReplacedFromOutsideWhileTheMicrophoneIsOn_ThenTheOldVoiceIsReleasedAndLaterFramesDropped()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.SetAudio(Caller(speaker), pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        var replacement = new GamePlayer { X = 0, Y = 0 };
        _context.Game.Players = new Dictionary<string, GamePlayer>(_context.Game.Players) { ["tab-1"] = replacement };

        // Act
        _context.Game.ReconcilePlayers();
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();
        Disconnect("tab-1", isNamed: false);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.False(speaker.IsMicrophoneOn);
        Assert.False(speaker.IsAudioOn);
        Assert.False(replacement.IsMicrophoneOn);
        Assert.Equal((false, false), _context.Game.GetVoicePages(speaker, 1));
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void WhenPlayersAreReplacedFromOutside_ThenTheNewInstanceKeepsItsOwnSpeaker()
    {
        // Arrange
        var speaker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        _context.Game.SetMicrophone(Caller(speaker), pageId: 1, isOn: true);
        var replacement = new GamePlayer();
        _context.Game.Players = new Dictionary<string, GamePlayer>(_context.Game.Players) { ["tab-1"] = replacement };
        _context.Game.ReconcilePlayers();
        replacement = _context.Game.Players["tab-1"];

        // Act
        _context.Game.SetMicrophone(Caller(replacement), pageId: 2, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 2, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.False(speaker.IsMicrophoneOn);
        Assert.True(replacement.IsMicrophoneOn);
        Assert.Single(sink.Frames);
    }

    [Fact]
    public void WhenAToggleChanges_ThenTheVolumeTableIsRebuilt()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        _context.Game.UpdateVolumeTable();
        _context.TimeProvider.Advance(GameVoiceSettings.VolumeTableInterval);
        var isRebuiltWithoutChange = _context.Game.UpdateVolumeTable();

        // Act
        _context.Game.SetAudio(Caller(player), pageId: 1, isOn: true);
        var isRebuilt = _context.Game.UpdateVolumeTable();

        // Assert
        Assert.False(isRebuiltWithoutChange);
        Assert.True(isRebuilt);
    }
}
