using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Sonos.Base.Services;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosPlayerOperationTests
{
    private static SonosPlayer CreateDisconnectedKitchen()
    {
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        return system.Players[TestFixtures.KitchenUuid];
    }

    [Fact]
    public async Task WhenSystemIsNotConnected_ThenPlayThrows()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => player.PlayAsync(CancellationToken.None));
        Assert.Contains("not connected", exception.Message);
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(11)]
    public async Task WhenBassIsOutOfRange_ThenThrows(int bass)
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SetBassAsync(bass, CancellationToken.None));
    }

    [Theory]
    [InlineData("SetVolume", -0.01)]
    [InlineData("SetVolume", 1.01)]
    [InlineData("SetVolume", 20)]
    [InlineData("RampVolume", -0.01)]
    [InlineData("RampVolume", 20)]
    [InlineData("ChangeVolume", -1.01)]
    [InlineData("ChangeVolume", 1.01)]
    [InlineData("ChangeVolume", 20)]
    [InlineData("PlayNotification", -0.01)]
    [InlineData("PlayNotification", 20)]
    public async Task WhenVolumeIsOutOfRange_ThenThrowsBeforeConnecting(string operation, double value)
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => InvokeVolumeOperation(player, operation, (decimal)value));
    }

    [Theory]
    [InlineData("SetVolume", 0)]
    [InlineData("SetVolume", 1)]
    [InlineData("RampVolume", 1)]
    [InlineData("ChangeVolume", -1)]
    [InlineData("ChangeVolume", 1)]
    [InlineData("PlayNotification", 0)]
    [InlineData("PlayNotification", 1)]
    public async Task WhenVolumeIsAtTheLimit_ThenItIsAccepted(string operation, double value)
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        // Not connected, so an accepted value fails at the connection instead of the argument check.
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeVolumeOperation(player, operation, (decimal)value));
    }

    private static Task InvokeVolumeOperation(SonosPlayer player, string operation, decimal value) => operation switch
    {
        "SetVolume" => player.SetVolumeAsync(value, CancellationToken.None),
        "RampVolume" => player.RampVolumeAsync(value, CancellationToken.None),
        "ChangeVolume" => player.ChangeVolumeAsync(value, CancellationToken.None),
        "PlayNotification" => player.PlayNotificationAsync("http://127.0.0.1/chime.mp3", value, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    [Fact]
    public async Task WhenJoiningUnknownRoom_ThenThrowsListingTheRooms()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => player.JoinGroupAsync("Nowhere", CancellationToken.None));
        Assert.Contains("Wohnzimmer", exception.Message);
    }

    [Fact]
    public async Task WhenPlayingUnknownFavorite_ThenThrows()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => player.PlayFavoriteAsync("Nothing", CancellationToken.None));
    }

    [Fact]
    public async Task WhenNightModeOnPlayerWithoutHomeTheater_ThenThrows()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.SetNightModeAsync(true, CancellationToken.None));
    }

    [Fact]
    public void WhenPlayerIsAloneAndHasNoDuration_ThenGroupAndSeekOperationsAreDisabled()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Assert
        Assert.False(player.LeaveGroup_IsEnabled);
        Assert.False(player.Seek_IsEnabled);
        Assert.False(player.SetNightMode_IsEnabled);
        Assert.False(player.Play_IsEnabled);
    }

    [Fact]
    public void WhenSystemIsNotConnected_ThenPlayerOperationsAreDisabled()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Assert
        Assert.All(GetPlayerOperationStates(player), state => Assert.False(state.IsEnabled, state.Name));
        Assert.All(GetCoordinatorOperationStates(player), state => Assert.False(state.IsEnabled, state.Name));
    }

    [Fact]
    public void WhenSystemIsConnected_ThenPlayerAndCoordinatorOperationsAreEnabled()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        var player = system.Players[TestFixtures.KitchenUuid];

        // Act
        system.IsConnected = true;

        // Assert
        Assert.All(GetPlayerOperationStates(player), state => Assert.True(state.IsEnabled, state.Name));
        Assert.All(GetCoordinatorOperationStates(player), state => Assert.True(state.IsEnabled, state.Name));
    }

    [Fact]
    public void WhenMembersCoordinatorIsOffline_ThenOnlyCoordinatorOperationsAreDisabled()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var coordinator = system.Players[TestFixtures.OfficeUuid];
        var member = system.Players[TestFixtures.KitchenUuid];
        Assert.All(GetCoordinatorOperationStates(member), state => Assert.True(state.IsEnabled, state.Name));

        // Act
        coordinator.ReportPollFailed("The speaker does not answer.");

        // Assert
        Assert.All(GetCoordinatorOperationStates(member), state => Assert.False(state.IsEnabled, state.Name));
        Assert.All(GetPlayerOperationStates(member), state => Assert.True(state.IsEnabled, state.Name));
        Assert.True(member.LeaveGroup_IsEnabled);
    }

    /// <summary>
    /// A connected household of the office coordinating a group with the kitchen.
    /// </summary>
    internal static SonosSystem CreateGroupedSystem()
    {
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(ZoneGroupStateParser.Parse($"""
            <ZoneGroupState><ZoneGroups>
              <ZoneGroup Coordinator="{TestFixtures.OfficeUuid}" ID="{TestFixtures.OfficeUuid}:1">
                <ZoneGroupMember UUID="{TestFixtures.OfficeUuid}" Location="http://10.0.0.116:1400/xml/device_description.xml" ZoneName="Büro" />
                <ZoneGroupMember UUID="{TestFixtures.KitchenUuid}" Location="http://10.0.0.121:1400/xml/device_description.xml" ZoneName="Küche" />
              </ZoneGroup>
            </ZoneGroups></ZoneGroupState>
            """));
        system.IsConnected = true;
        return system;
    }

    // Commands sent to the player itself. SwitchToTv, SwitchToLineIn, SetNightMode and SetSpeechEnhancement also
    // need a capability the fixture players lack, and Seek needs a track duration, so they are covered elsewhere.
    private static (string Name, bool IsEnabled)[] GetPlayerOperationStates(SonosPlayer player) =>
    [
        (nameof(SonosPlayer.SetVolume_IsEnabled), player.SetVolume_IsEnabled),
        (nameof(SonosPlayer.ChangeVolume_IsEnabled), player.ChangeVolume_IsEnabled),
        (nameof(SonosPlayer.RampVolume_IsEnabled), player.RampVolume_IsEnabled),
        (nameof(SonosPlayer.Mute_IsEnabled), player.Mute_IsEnabled),
        (nameof(SonosPlayer.Unmute_IsEnabled), player.Unmute_IsEnabled),
        (nameof(SonosPlayer.PlayNotification_IsEnabled), player.PlayNotification_IsEnabled),
        (nameof(SonosPlayer.SetBass_IsEnabled), player.SetBass_IsEnabled),
        (nameof(SonosPlayer.SetTreble_IsEnabled), player.SetTreble_IsEnabled),
        (nameof(SonosPlayer.SetLoudness_IsEnabled), player.SetLoudness_IsEnabled),
        (nameof(SonosPlayer.JoinGroup_IsEnabled), player.JoinGroup_IsEnabled)
    ];

    // Commands routed to the group coordinator.
    private static (string Name, bool IsEnabled)[] GetCoordinatorOperationStates(SonosPlayer player) =>
    [
        (nameof(SonosPlayer.Play_IsEnabled), player.Play_IsEnabled),
        (nameof(SonosPlayer.Pause_IsEnabled), player.Pause_IsEnabled),
        (nameof(SonosPlayer.Stop_IsEnabled), player.Stop_IsEnabled),
        (nameof(SonosPlayer.Next_IsEnabled), player.Next_IsEnabled),
        (nameof(SonosPlayer.Previous_IsEnabled), player.Previous_IsEnabled),
        (nameof(SonosPlayer.TogglePlayback_IsEnabled), player.TogglePlayback_IsEnabled),
        (nameof(SonosPlayer.PlayFavorite_IsEnabled), player.PlayFavorite_IsEnabled),
        (nameof(SonosPlayer.PlayUri_IsEnabled), player.PlayUri_IsEnabled),
        (nameof(SonosPlayer.PlayStream_IsEnabled), player.PlayStream_IsEnabled),
        (nameof(SonosPlayer.SetShuffle_IsEnabled), player.SetShuffle_IsEnabled),
        (nameof(SonosPlayer.SetRepeat_IsEnabled), player.SetRepeat_IsEnabled),
        (nameof(SonosPlayer.SetSleepTimer_IsEnabled), player.SetSleepTimer_IsEnabled)
    ];

    [Fact]
    public async Task WhenSettingVolume_ThenSonosVolumeIsSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.SetVolumeAsync(0.5m, CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "SetVolume" && call.Body.Contains("<DesiredVolume>50</DesiredVolume>"));
        Assert.True(connected.Player.Play_IsEnabled);
    }

    [Fact]
    public async Task WhenPlayingStreamFavorite_ThenUriWithStoredMetadataIsSetAndPlayed()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayFavoriteAsync("radio fm1", CancellationToken.None);

        // Assert
        var calls = speaker.Calls.ToArray();
        var setUri = Array.FindIndex(calls, call => call.Action == "SetAVTransportURI" && call.Body.Contains("tunein%3a9557"));
        var play = Array.FindLastIndex(calls, call => call.Action == "Play");
        Assert.True(setUri >= 0, "SetAVTransportURI with the favorite URI was not sent.");
        Assert.True(play > setUri, "Play was not sent after setting the URI.");
        Assert.Contains("Radio FM1", calls[setUri].Body);
    }

    [Fact]
    public async Task WhenPlayingHttpUri_ThenUriIsSentUnchangedWithoutMetadataAndPlayFollows()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayUriAsync("http://files.example.com/chime.mp3", CancellationToken.None);

        // Assert
        var calls = speaker.Calls.ToList();
        var setUri = calls.FindIndex(call => call.Action == "SetAVTransportURI");
        Assert.True(setUri >= 0, "SetAVTransportURI was not sent.");
        Assert.True(calls.FindIndex(call => call.Action == "Play") > setUri, "Play was not sent after setting the URI.");
        var (transportUri, metadata) = ReadTransportUri(calls[setUri]);
        Assert.Equal("http://files.example.com/chime.mp3", transportUri);
        Assert.Equal(string.Empty, metadata);
    }

    [Fact]
    public async Task WhenPlayingHttpStream_ThenRadioSchemeAndEscapedBroadcastMetadataAreSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayStreamAsync("http://stream.example.com/live.mp3", "Rock & Roll", CancellationToken.None);

        // Assert
        var setUri = Assert.Single(speaker.Calls, call => call.Action == "SetAVTransportURI");
        var (transportUri, metadata) = ReadTransportUri(setUri);
        Assert.Equal("x-rincon-mp3radio://stream.example.com/live.mp3", transportUri);
        Assert.Contains("<dc:title>Rock &amp; Roll</dc:title>", metadata);
        Assert.Contains("<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>", metadata);
        Assert.Contains(speaker.Calls, call => call.Action == "Play");
    }

    [Theory]
    [InlineData("ftp://stream.example.com/live.mp3")]
    [InlineData("x-sonos-spotify:spotify%3atrack%3a1?sid=9")]
    [InlineData("not a uri")]
    public async Task WhenStreamUriHasUnsupportedScheme_ThenThrowsBeforeAnyCall(string uri)
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var callsBefore = speaker.Calls.Count;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => connected.Player.PlayStreamAsync(uri, "Live", CancellationToken.None));
        Assert.Equal("uri", exception.ParamName);
        Assert.Equal(callsBefore, speaker.Calls.Count);
    }

    [Fact]
    public async Task WhenTogglingWhilePaused_ThenPlayIsSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.TogglePlaybackAsync(CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "Play");
        Assert.DoesNotContain(speaker.Calls, call => call.Action == "Pause");
    }

    [Fact]
    public async Task WhenSettingShuffle_ThenRepeatIsKept()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        speaker.Respond("GetTransportSettings", ("PlayMode", "REPEAT_ALL"), ("RecQualityMode", "NOT_IMPLEMENTED"));
        await connected.System.RefreshAsync(CancellationToken.None);
        Assert.Equal(SonosRepeatMode.All, connected.Player.Repeat);

        // Act
        await connected.Player.SetShuffleAsync(true, CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "SetPlayMode" && call.Body.Contains("<NewPlayMode>SHUFFLE</NewPlayMode>"));
    }

    [Fact]
    public async Task WhenMemberSetsShuffle_ThenCoordinatorRepeatIsKeptAndCoordinatorIsCommanded()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true);
        household.Office.Respond("GetTransportSettings", ("PlayMode", "REPEAT_ALL"), ("RecQualityMode", "NOT_IMPLEMENTED"));
        await household.System.RefreshAsync(CancellationToken.None);
        Assert.Equal(SonosRepeatMode.All, household.OfficePlayer.Repeat);
        Assert.Equal(SonosRepeatMode.Off, household.KitchenPlayer.Repeat);

        // Act
        await household.KitchenPlayer.SetShuffleAsync(true, CancellationToken.None);

        // Assert
        Assert.Contains(household.Office.Calls, call => call.Action == "SetPlayMode" && call.Body.Contains("<NewPlayMode>SHUFFLE</NewPlayMode>"));
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == "SetPlayMode");
    }

    [Fact]
    public async Task WhenMemberPlays_ThenPlayIsSentToTheCoordinator()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true);

        // Act
        await household.KitchenPlayer.PlayAsync(CancellationToken.None);

        // Assert
        Assert.Contains(household.Office.Calls, call => call.Action == "Play");
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == "Play");
    }

    [Fact]
    public async Task WhenPlayersAreGrouped_ThenLeaveGroupIsEnabledForEveryMember()
    {
        // Act
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true);

        // Assert
        Assert.True(household.KitchenPlayer.LeaveGroup_IsEnabled);
        Assert.True(household.OfficePlayer.LeaveGroup_IsEnabled);
    }

    [Fact]
    public async Task WhenConnectedPlayerIsAlone_ThenLeaveGroupIsDisabled()
    {
        // Act
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Assert
        Assert.False(connected.Player.LeaveGroup_IsEnabled);
    }

    [Fact]
    public async Task WhenJoiningTheGroupThePlayerIsIn_ThenNothingIsSent()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true);

        // Act
        await household.KitchenPlayer.JoinGroupAsync("Büro", CancellationToken.None);
        await household.OfficePlayer.JoinGroupAsync("Küche", CancellationToken.None);

        // Assert
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == "SetAVTransportURI");
        Assert.DoesNotContain(household.Office.Calls, call => call.Action == "SetAVTransportURI");
    }

    [Fact]
    public void WhenSystemIsNotConnected_ThenPlayReturnsFaultedTask()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act
        var task = player.PlayAsync(CancellationToken.None);

        // Assert
        Assert.IsType<InvalidOperationException>(task.Exception?.InnerException);
    }

    [Fact]
    public async Task WhenNameArgumentsAreNull_ThenThrowArgumentNull()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        Assert.Equal("room", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.JoinGroupAsync(null!, CancellationToken.None))).ParamName);
        Assert.Equal("title", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.PlayFavoriteAsync(null!, CancellationToken.None))).ParamName);
        Assert.Equal("uri", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.PlayUriAsync(null!, CancellationToken.None))).ParamName);
        Assert.Equal("uri", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.PlayStreamAsync(null!, null, CancellationToken.None))).ParamName);
        Assert.Equal("soundUri", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.PlayNotificationAsync(null!, 0.5m, CancellationToken.None))).ParamName);
    }

    [Fact]
    public async Task WhenPlayingNativeUri_ThenUriIsSentUnchangedWithoutMetadata()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayUriAsync("x-sonos-spotify:spotify%3atrack%3a1?sid=9", CancellationToken.None);

        // Assert
        var setUri = Assert.Single(speaker.Calls, call => call.Action == "SetAVTransportURI");
        var (transportUri, metadata) = ReadTransportUri(setUri);
        Assert.Equal("x-sonos-spotify:spotify%3atrack%3a1?sid=9", transportUri);
        Assert.Equal(string.Empty, metadata);
    }

    [Fact]
    public async Task WhenPlayingRadioStreamUriWithTitle_ThenUriIsKeptAndTitleMetadataIsSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayStreamAsync("x-rincon-mp3radio://stream.example.com/live.mp3", "Live", CancellationToken.None);

        // Assert
        var setUri = Assert.Single(speaker.Calls, call => call.Action == "SetAVTransportURI");
        var (transportUri, metadata) = ReadTransportUri(setUri);
        Assert.Equal("x-rincon-mp3radio://stream.example.com/live.mp3", transportUri);
        Assert.Contains("<dc:title>Live</dc:title>", metadata);
        Assert.Contains("object.item.audioItem.audioBroadcast", metadata);
    }

    // The metadata is DIDL-Lite carried as the text of the SOAP element, so parsing the envelope unescapes it once.
    private static (string Uri, string Metadata) ReadTransportUri(SoapCall call)
    {
        var body = System.Xml.Linq.XDocument.Parse(call.Body);
        string Read(string name) => body.Descendants().Single(element => element.Name.LocalName == name).Value;
        return (Read("CurrentURI"), Read("CurrentURIMetaData"));
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("file:///tmp/chime.mp3")]
    public async Task WhenNotificationUriIsNotHttp_ThenThrowsBeforeAnyCall(string soundUri)
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var callCount = speaker.Calls.Count;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => connected.Player.PlayNotificationAsync(soundUri, 0.5m, CancellationToken.None));
        Assert.Equal(callCount, speaker.Calls.Count);
    }

    [Fact]
    public async Task WhenSeekingToNegativePosition_ThenThrows()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SeekAsync(TimeSpan.FromSeconds(-1), CancellationToken.None));
    }

    [Fact]
    public async Task WhenSettingNegativeSleepTimer_ThenThrows()
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SetSleepTimerAsync(TimeSpan.FromMinutes(-1), CancellationToken.None));
    }

    [Theory]
    [InlineData(24 * 3600)]
    [InlineData(25 * 3600)]
    public async Task WhenSettingSleepTimerAboveTheMaximum_ThenThrows(int seconds)
    {
        // Arrange
        var player = CreateDisconnectedKitchen();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => player.SetSleepTimerAsync(TimeSpan.FromSeconds(seconds), CancellationToken.None));
    }

    [Fact]
    public async Task WhenCommandFails_ThenFaultPropagatesAndStateIsReadBack()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        speaker.RespondWithFault("Play", 701);

        // Act
        var exception = await Assert.ThrowsAsync<SonosServiceException>(() => connected.Player.PlayFavoriteAsync("radio fm1", CancellationToken.None));

        // Assert
        Assert.Equal(701, exception.UpnpErrorCode);
        var actions = speaker.Calls.Select(call => call.Action).ToList();
        Assert.True(actions.LastIndexOf("GetTransportInfo") > actions.LastIndexOf("Play"), "The player state was not read back after the failed command.");
    }
}
