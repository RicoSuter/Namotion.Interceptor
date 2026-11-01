using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Game.Voice;

namespace HomeBlaze.Game.Tests;

public class GameVoiceTests
{
    private readonly GameTestContext _context = new();

    // Switches and publishes, so the subject shows the result.
    private bool SetMicrophone(GamePlayer player, int pageId, bool isOn) => Run(_context.Game.SetMicrophoneAsync(player.ClientId, pageId, isOn));

    private bool SetAudio(GamePlayer player, int pageId, bool isOn) => Run(_context.Game.SetAudioAsync(player.ClientId, pageId, isOn));

    private bool Run(Task<bool> task)
    {
        var result = _context.RunAsync(task).GetAwaiter().GetResult();
        _context.Publish();
        return result;
    }

    private (bool IsMicrophonePage, bool IsAudioPage) GetVoicePages(GamePlayer player, int pageId) =>
        _context.RunAsync(_context.Game.GetVoicePagesAsync(player.ClientId, pageId)).GetAwaiter().GetResult();

    // Lets the volume table follow the changes, then mixes one frame with it.
    private void Mix()
    {
        _context.Tick((int)(GameVoiceSettings.VolumeTableInterval / GameSimulationSettings.TickInterval));
        _context.Game.VoiceMixer.Mix(_context.Game.VolumeTable);
    }

    private void Disconnect(string clientId, bool isNamed)
    {
        _context.Clients.Set(clientId, isNamed, isConnected: false);
        _context.Reconcile();
    }

    [Fact]
    public void WhenThePlayerTurnsTheMicrophoneOn_ThenItIsOnAndItsFramesReachTheListener()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);

        // Act
        var isAccepted = SetMicrophone(talker, pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.True(isAccepted);
        Assert.True(talker.IsMicrophoneOn);
        Assert.Equal(400, VoiceFrames.ReadLastSample(Assert.Single(sink.Frames)));
    }

    [Fact]
    public void WhenASecondPageTurnsTheMicrophoneOn_ThenOnlyItsFramesCount()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        SetMicrophone(talker, pageId: 1, isOn: true);

        // Act
        SetMicrophone(talker, pageId: 2, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        _context.Game.VoiceMixer.Receive("tab-1", 2, VoiceFrames.Create(2000));
        Mix();

        // Assert
        Assert.Equal(800, VoiceFrames.ReadLastSample(Assert.Single(sink.Frames)));
    }

    [Fact]
    public void WhenAnOlderPageTurnsTheMicrophoneOff_ThenTheLatestPageKeepsIt()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        SetMicrophone(talker, pageId: 1, isOn: true);
        SetMicrophone(talker, pageId: 2, isOn: true);

        // Act
        SetMicrophone(talker, pageId: 1, isOn: false);

        // Assert
        Assert.True(talker.IsMicrophoneOn);
        Assert.Equal((false, false), GetVoicePages(talker, 1));
        Assert.Equal((true, false), GetVoicePages(talker, 2));
    }

    [Fact]
    public void WhenTwoPagesTurnTheSpeakerOn_ThenAudioStaysOnUntilBothTurnItOff()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        SetAudio(player, pageId: 1, isOn: true);
        SetAudio(player, pageId: 2, isOn: true);

        // Act
        SetAudio(player, pageId: 1, isOn: false);
        var isOnWithOnePage = player.IsAudioOn;
        SetAudio(player, pageId: 2, isOn: false);

        // Assert
        Assert.True(isOnWithOnePage);
        Assert.False(player.IsAudioOn);
    }

    [Fact]
    public void WhenTheCallersClientHasNoPlayerInTheGame_ThenTheTogglesAreRefused()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var stranger = new GameCaller("tab-2");

        // Act
        var isMicrophoneAccepted = Run(_context.Game.SetMicrophoneAsync(stranger.ClientId, pageId: 1, isOn: true));
        var isAudioAccepted = Run(_context.Game.SetAudioAsync(stranger.ClientId, pageId: 1, isOn: true));

        // Assert
        Assert.False(isMicrophoneAccepted);
        Assert.False(isAudioAccepted);
        Assert.False(player.IsMicrophoneOn || player.IsAudioOn);
    }

    [Fact]
    public void WhenADisconnectedPlayerTurnsTheMicrophoneOn_ThenItIsRefusedButTurningOffWorks()
    {
        // Arrange
        var kitchen = _context.AddConnectedPlayer("kitchen", isNamed: true);
        Disconnect("kitchen", isNamed: true);

        // Act
        var isTurnedOn = SetMicrophone(kitchen, pageId: 1, isOn: true);
        var isTurnedOff = SetMicrophone(kitchen, pageId: 1, isOn: false);

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
        SetMicrophone(kitchen, pageId: 1, isOn: true);
        SetAudio(kitchen, pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("kitchen", 1, VoiceFrames.Create(1000));

        // Act
        Disconnect("kitchen", isNamed: true);
        Mix();

        // Assert
        Assert.False(kitchen.IsMicrophoneOn);
        Assert.False(kitchen.IsAudioOn);
        Assert.Equal((false, false), GetVoicePages(kitchen, 1));
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void WhenAnAnonymousPlayerLeaves_ThenItsQueuedAndLaterFramesAreDropped()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        SetMicrophone(talker, pageId: 1, isOn: true);
        SetAudio(talker, pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));

        // Act
        Disconnect("tab-1", isNamed: false);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.False(_context.Game.Players.ContainsKey("tab-1"));
        Assert.False(talker.IsMicrophoneOn);
        Assert.False(talker.IsAudioOn);
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void WhenADisconnectedPlayerTurnsTheSpeakerOn_ThenItIsRefusedButTurningOffWorks()
    {
        // Arrange
        var kitchen = _context.AddConnectedPlayer("kitchen", isNamed: true);
        Disconnect("kitchen", isNamed: true);

        // Act
        var isTurnedOn = SetAudio(kitchen, pageId: 1, isOn: true);
        var isTurnedOff = SetAudio(kitchen, pageId: 1, isOn: false);

        // Assert
        Assert.False(isTurnedOn);
        Assert.True(isTurnedOff);
        Assert.False(kitchen.IsAudioOn);
    }

    [Fact]
    public void WhenPlayersAreReplacedFromOutsideWhileTheMicrophoneIsOn_ThenTheOldVoiceIsReleasedAndLaterFramesDropped()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        SetMicrophone(talker, pageId: 1, isOn: true);
        SetAudio(talker, pageId: 1, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        var replacement = new GamePlayer { X = 0, Y = 0 };
        _context.Game.Players = new Dictionary<string, GamePlayer>(_context.Game.Players) { ["tab-1"] = replacement };

        // Act
        _context.Tick();
        _context.Reconcile();
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();
        Disconnect("tab-1", isNamed: false);
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.False(talker.IsMicrophoneOn);
        Assert.False(talker.IsAudioOn);
        Assert.False(replacement.IsMicrophoneOn);
        Assert.Equal((false, false), GetVoicePages(talker, 1));
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public void WhenPlayersAreReplacedFromOutside_ThenTheNewInstanceCanSpeak()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 10);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        SetMicrophone(talker, pageId: 1, isOn: true);
        var replacement = new GamePlayer();
        _context.Game.Players = new Dictionary<string, GamePlayer>(_context.Game.Players) { ["tab-1"] = replacement };
        _context.Tick();
        _context.Reconcile();
        replacement = _context.Game.Players["tab-1"];

        // Act
        SetMicrophone(replacement, pageId: 2, isOn: true);
        _context.Game.VoiceMixer.Receive("tab-1", 2, VoiceFrames.Create(1000));
        Mix();

        // Assert
        Assert.False(talker.IsMicrophoneOn);
        Assert.True(replacement.IsMicrophoneOn);
        Assert.Single(sink.Frames);
    }

    [Fact]
    public void WhenAToggleChanges_ThenTheVolumeTableIsRebuilt()
    {
        // Arrange
        var player = _context.AddConnectedPlayer("tab-1");
        var rebuilds = 0;
        _context.Game.VolumeTableChanged += () => rebuilds++;
        _context.Publish();
        var rebuildsWithoutChange = rebuilds;

        // Act
        SetAudio(player, pageId: 1, isOn: true);

        // Assert
        Assert.Equal(0, rebuildsWithoutChange);
        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public void WhenAVolumeTableChangedHandlerThrows_ThenTheTickGoesOn()
    {
        // Arrange
        var talker = _context.AddConnectedPlayer("tab-1");
        _context.AddConnectedPlayer("tab-2", x: 20);
        var sink = new RecordingVoiceSink();
        using var registration = _context.Game.VoiceMixer.RegisterSink("tab-2", sink);
        var calls = 0;
        _context.Game.VolumeTableChanged += () =>
        {
            if (++calls == 1)
            {
                throw new InvalidOperationException("The handler failed.");
            }
        };

        // Act
        var exception = Record.Exception(() => SetMicrophone(talker, pageId: 1, isOn: true));
        _context.Game.VoiceMixer.Receive("tab-1", 1, VoiceFrames.Create(1000));
        _context.Game.TickVoice();

        // Assert
        Assert.Null(exception);
        Assert.Equal(1, calls);
        Assert.True(talker.IsMicrophoneOn);
        Assert.Equal(200, VoiceFrames.ReadLastSample(Assert.Single(sink.Frames)));
    }
}
