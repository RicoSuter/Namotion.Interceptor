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

        // Act
        await connected.Player.SetShuffleAsync(true, CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "SetPlayMode" && call.Body.Contains("<NewPlayMode>SHUFFLE_NOREPEAT</NewPlayMode>"));
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
