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
        // Act
        var player = CreateDisconnectedKitchen();

        // Assert
        Assert.False(player.LeaveGroup_IsEnabled);
        Assert.False(player.Seek_IsEnabled);
        Assert.False(player.SetNightMode_IsEnabled);
        Assert.False(player.Play_IsEnabled);
    }

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
    public async Task WhenPlayingHttpUri_ThenRadioSchemeAndEscapedTitleAreSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayUriAsync("http://stream.example.com/live.mp3", "Rock & Roll", CancellationToken.None);

        // Assert
        var setUri = Assert.Single(speaker.Calls, call => call.Action == "SetAVTransportURI");
        Assert.Contains("x-rincon-mp3radio://stream.example.com/live.mp3", setUri.Body);
        Assert.Contains("Rock &amp;amp; Roll", setUri.Body);
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
        Assert.Equal("roomNameOrUuid", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.JoinGroupAsync(null!, CancellationToken.None))).ParamName);
        Assert.Equal("name", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.PlayFavoriteAsync(null!, CancellationToken.None))).ParamName);
        Assert.Equal("uri", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.PlayUriAsync(null!, null, CancellationToken.None))).ParamName);
        Assert.Equal("soundUri", (await Assert.ThrowsAsync<ArgumentNullException>(() => player.PlayNotificationAsync(null!, 0.5m, CancellationToken.None))).ParamName);
    }

    [Fact]
    public async Task WhenPlayingNativeUriWithTitle_ThenMetadataIsEmpty()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayUriAsync("x-sonos-spotify:spotify%3atrack%3a1?sid=9", "Song", CancellationToken.None);

        // Assert
        var setUri = Assert.Single(speaker.Calls, call => call.Action == "SetAVTransportURI");
        Assert.Contains("x-sonos-spotify:spotify%3atrack%3a1?sid=9", setUri.Body);
        Assert.DoesNotContain("DIDL-Lite", setUri.Body);
    }

    [Fact]
    public async Task WhenPlayingRadioUriWithTitle_ThenTitleMetadataIsSent()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.Player.PlayUriAsync("x-rincon-mp3radio://stream.example.com/live.mp3", "Live", CancellationToken.None);

        // Assert
        var setUri = Assert.Single(speaker.Calls, call => call.Action == "SetAVTransportURI");
        Assert.Contains("DIDL-Lite", setUri.Body);
        Assert.Contains("Live", setUri.Body);
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
