using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

public class SonosPlayerStateTests
{
    private const string SpotifyUri = TestFixtures.SpotifyConnectUri;

    private static AvTransportChange SpotifyPlaying() => new(
        "PLAYING",
        "SHUFFLE_NOREPEAT",
        SpotifyUri,
        SpotifyUri,
        "0:03:25",
        SonosEventBodies.Didl("Song", "Artist", "Album", "/getaa?s=1&u=x"));

    private static AvTransportChange QueueTrackPlaying() => new(
        "PLAYING",
        "NORMAL",
        "x-rincon-queue:RINCON_A0000000000601400#0",
        "x-file-cifs://nas/music/song-a.mp3",
        "0:03:30",
        SonosEventBodies.Didl("Song A", "Artist A", "Album A", "/getaa?s=1&u=a"));

    private static AvTransportChange RadioPlaying(string mediaUri, string mediaMetaData) =>
        new("PLAYING", "NORMAL", mediaUri, mediaUri, null, null, mediaMetaData);

    private static SonosPlayerReading Reading(AvTransportChange avTransport, RenderingControlChange? renderingControl = null) =>
        new(avTransport, TimeSpan.FromSeconds(42), null, renderingControl ?? EmptyRenderingControl);

    [Fact]
    public void WhenOlderPollCompletesAfterNewerPoll_ThenOlderPollIsDropped()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyPoll(Reading(SpotifyPlaying(), new RenderingControlChange(50, null, null, null, null, null, null)), T0 + 2);

        // Act
        player.ApplyPoll(
            new SonosPlayerReading(
                new AvTransportChange("PAUSED_PLAYBACK", null, null, null, null, null),
                TimeSpan.FromSeconds(7),
                null,
                new RenderingControlChange(10, null, null, null, null, null, null)),
            T0 + 1);

        // Assert
        Assert.Equal(0.5m, player.Volume);
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
        Assert.Equal(TimeSpan.FromSeconds(42), player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenTheWallClockStepsBack_ThenLaterPollsAreStillApplied()
    {
        // Arrange
        var clock = new SteppableClock();
        var system = CreateHousehold(clock);
        var player = system.Players[KitchenUuid];
        player.ApplyPoll(Reading(SpotifyPlaying(), new RenderingControlChange(50, null, null, null, null, null, null)), system.NextOrder());

        // Act
        clock.WallClockOffset = TimeSpan.FromMinutes(-10);
        player.ApplyPoll(Reading(SpotifyPlaying(), new RenderingControlChange(10, null, null, null, null, null, null)), system.NextOrder());
        player.ApplyPoll(Reading(SpotifyPlaying(), new RenderingControlChange(20, null, null, null, null, null, null)), system.NextOrder());

        // Assert
        Assert.Equal(0.2m, player.Volume);
    }

    [Fact]
    public void WhenAvTransportEventApplied_ThenTrackStateUpdates()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];

        // Act
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
        Assert.True(player.IsPlaying);
        Assert.True(player.Shuffle);
        Assert.Equal(SonosRepeatMode.Off, player.Repeat);
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal("Artist", player.CurrentTrackArtist);
        Assert.Equal("Album", player.CurrentTrackAlbum);
        Assert.Equal("http://10.0.0.121:1400/getaa?s=1&u=x", player.CurrentTrackImageUri);
        Assert.Equal(TimeSpan.FromSeconds(205), player.CurrentTrackDuration);
        Assert.Equal(SonosSource.SpotifyConnect, player.Source);
    }

    [Fact]
    public void WhenPollReportsNotImplementedMetadata_ThenEventMetadataIsKept()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange("PLAYING", "SHUFFLE_NOREPEAT", SpotifyUri, SpotifyUri, "NOT_IMPLEMENTED", "NOT_IMPLEMENTED")), T0 + 1);

        // Assert
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal(TimeSpan.FromSeconds(205), player.CurrentTrackDuration);
        Assert.Equal(TimeSpan.FromSeconds(42), player.CurrentTrackPosition);
    }

    [Theory]
    [InlineData(SpotifyUri)]
    [InlineData("x-sonos-htastream:RINCON_A0000000000601400:spdif")]
    public void WhenPollReportsAnotherTrackWithoutMetadata_ThenThePreviousTrackDetailsAreCleared(string trackUri)
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(QueueTrackPlaying(), T0);

        // Act
        player.ApplyPoll(
            new SonosPlayerReading(new AvTransportChange("PLAYING", "NORMAL", trackUri, trackUri, "NOT_IMPLEMENTED", "NOT_IMPLEMENTED"), null, null, EmptyRenderingControl),
            T0 + 1);

        // Assert
        Assert.Equal(trackUri, player.CurrentTrackUri);
        Assert.Null(player.CurrentTrackTitle);
        Assert.Null(player.CurrentTrackArtist);
        Assert.Null(player.CurrentTrackAlbum);
        Assert.Null(player.CurrentTrackImageUri);
        Assert.Null(player.CurrentTrackDuration);
        Assert.Null(player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenStationMetadataArrives_ThenTheMediaTitleIsTheStation()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];

        // Act
        player.ApplyAvTransportEvent(RadioPlaying("x-sonosapi-stream:s1", SonosEventBodies.Didl("SRF 3")), T0);

        // Assert
        Assert.Equal("SRF 3", player.MediaTitle);
    }

    [Fact]
    public void WhenPollReportsTheSameMediaWithoutMetadata_ThenTheMediaTitleIsKept()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(RadioPlaying("x-sonosapi-stream:s1", SonosEventBodies.Didl("SRF 3")), T0);

        // Act
        player.ApplyPoll(new SonosPlayerReading(RadioPlaying("x-sonosapi-stream:s1", ""), null, null, EmptyRenderingControl), T0 + 1);

        // Assert
        Assert.Equal("SRF 3", player.MediaTitle);
    }

    [Fact]
    public void WhenTheTrackChangesWithinTheSameMedia_ThenTheMediaTitleIsKept()
    {
        // Arrange
        const string queueUri = "x-rincon-queue:RINCON_A0000000000601400#0";
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(new AvTransportChange("PLAYING", "NORMAL", queueUri, "x-file-cifs://nas/a.mp3", null, null, SonosEventBodies.Didl("Playlist")), T0);

        // Act
        player.ApplyPoll(
            new SonosPlayerReading(new AvTransportChange("PLAYING", "NORMAL", queueUri, "x-file-cifs://nas/b.mp3", null, null, ""), null, null, EmptyRenderingControl),
            T0 + 1);

        // Assert
        Assert.Equal("x-file-cifs://nas/b.mp3", player.CurrentTrackUri);
        Assert.Equal("Playlist", player.MediaTitle);
    }

    [Fact]
    public void WhenOtherMediaArrivesWithoutMetadata_ThenTheMediaTitleIsCleared()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(RadioPlaying("x-sonosapi-stream:s1", SonosEventBodies.Didl("SRF 3")), T0);

        // Act
        player.ApplyPoll(new SonosPlayerReading(RadioPlaying("x-sonosapi-stream:s2", "NOT_IMPLEMENTED"), null, null, EmptyRenderingControl), T0 + 1);

        // Assert
        Assert.Null(player.MediaTitle);
    }

    [Fact]
    public void WhenEventChangesTheTrack_ThenThePositionOfThePreviousTrackIsCleared()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyPoll(Reading(QueueTrackPlaying()), T0);

        // Act
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0 + 1);

        // Assert
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Null(player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenEventKeepsTheTrack_ThenThePositionIsKept()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyPoll(Reading(SpotifyPlaying()), T0);

        // Act
        player.ApplyAvTransportEvent(new AvTransportChange("PAUSED_PLAYBACK", null, null, null, null, null), T0 + 1);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(42), player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenPollStartedBeforeAnAvTransportEvent_ThenItsPositionIsNotApplied()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyPoll(
            new SonosPlayerReading(SpotifyPlaying(), TimeSpan.FromSeconds(10), null, EmptyRenderingControl),
            T0 - 10);
        player.ApplyAvTransportEvent(new AvTransportChange("PLAYING", null, null, null, null, null), T0 + 1);

        // Act
        player.ApplyPoll(Reading(SpotifyPlaying()), T0);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(10), player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenEventReportsNotImplementedTransportState_ThenTransportStateIsKept()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyAvTransportEvent(new AvTransportChange("NOT_IMPLEMENTED", null, null, null, null, null), T0 + 1);

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
    }

    [Fact]
    public void WhenPollReportsNotImplementedTransportState_ThenTransportStateIsKept()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange("NOT_IMPLEMENTED", null, null, null, null, null)), T0 + 1);

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
    }

    [Fact]
    public void WhenSameMetadataArrivesAfterIpChange_ThenImageUriFollowsTheNewAddress()
    {
        // Arrange
        var system = CreateSystem();
        var household = ReadHousehold();
        system.ApplyTopology(household);
        var player = system.Players[TestFixtures.KitchenUuid];
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);
        system.ApplyTopology(new SonosTopology(household.Groups
            .Select(group => group with
            {
                Players = group.Players
                    .Select(member => member.Uuid == TestFixtures.KitchenUuid ? member with { BaseUri = new Uri("http://10.0.0.199:1400/") } : member)
                    .ToArray()
            })
            .ToArray()));

        // Act
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0 + 1);

        // Assert
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal("http://10.0.0.199:1400/getaa?s=1&u=x", player.CurrentTrackImageUri);
    }

    [Fact]
    public void WhenPollStartedBeforeEvent_ThenPollDoesNotOverwriteTheEvent()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0 + 1);

        // Act
        player.ApplyPoll(
            Reading(new AvTransportChange("STOPPED", "NORMAL", "", "", "", ""), new RenderingControlChange(10, false, 0, 0, true, null, null)),
            T0);

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal(0.1m, player.Volume);
        Assert.Null(player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenPollStartedAfterEvent_ThenPollApplies()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange("STOPPED", "NORMAL", "", "", "0:00:00", "")), T0 + 1);

        // Assert
        Assert.Equal(SonosTransportState.Stopped, player.TransportState);
        Assert.False(player.IsPlaying);
        Assert.Null(player.CurrentTrackTitle);
        Assert.Null(player.CurrentTrackUri);
        Assert.Equal(SonosSource.None, player.Source);
    }

    [Fact]
    public void WhenRenderingControlEventApplied_ThenValuesAreConverted()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];

        // Act
        player.ApplyRenderingControlEvent(new RenderingControlChange(44, true, -2, 3, false, null, null), T0);

        // Assert
        Assert.Equal(0.44m, player.Volume);
        Assert.True(player.IsMuted);
        Assert.Equal(-2, player.Bass);
        Assert.Equal(3, player.Treble);
        Assert.False(player.Loudness);
        Assert.Null(player.NightMode);
    }

    [Fact]
    public void WhenRenderingControlReportsMute_ThenVolumeStateReportsMuted()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];

        // Act
        player.ApplyRenderingControlEvent(new RenderingControlChange(44, true, null, null, null, null, null), T0);

        // Assert
        IVolumeState volumeState = player;
        Assert.True(volumeState.IsMuted);
    }

    [Fact]
    public void WhenRenderingControlPollStartedBeforeEvent_ThenVolumeFromEventIsKept()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        player.ApplyRenderingControlEvent(new RenderingControlChange(44, null, null, null, null, null, null), T0 + 1);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange(null, null, null, null, null, null), new RenderingControlChange(10, false, 0, 0, true, null, null)), T0);

        // Assert
        Assert.Equal(0.44m, player.Volume);
    }

    [Fact]
    public void WhenTvIsPlaying_ThenSourceIsTv()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];
        const string tvUri = "x-sonos-htastream:RINCON_A0000000000601400:spdif";

        // Act
        player.ApplyAvTransportEvent(new AvTransportChange("PLAYING", null, tvUri, tvUri, null, null), T0);

        // Assert
        Assert.Equal(SonosSource.Tv, player.Source);
    }

    [Fact]
    public void WhenSleepTimerPolled_ThenRemainingTimeIsSet()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];

        // Act
        player.ApplyPoll(new SonosPlayerReading(
            new AvTransportChange(null, null, null, null, null, null),
            null,
            TimeSpan.FromMinutes(30),
            EmptyRenderingControl), T0);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(30), player.SleepTimerRemaining);
    }
}
