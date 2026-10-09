using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosPlayerStateTests
{
    private const string SpotifyUri = "x-sonos-vli:RINCON_A0000000000601400:2,spotify:94963e711df088cf";
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static SonosPlayer CreateKitchen()
    {
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
        return system.Players[TestFixtures.KitchenUuid];
    }

    private static AvTransportChange SpotifyPlaying() => new(
        "PLAYING",
        "SHUFFLE_NOREPEAT",
        SpotifyUri,
        SpotifyUri,
        "0:03:25",
        SonosEventBodies.Didl("Song", "Artist", "Album", "/getaa?s=1&u=x"));

    private static SonosPlayerReading Reading(AvTransportChange avTransport, RenderingControlChange? renderingControl = null) =>
        new(avTransport, TimeSpan.FromSeconds(42), null, renderingControl ?? new RenderingControlChange(null, null, null, null, null, null, null));

    [Fact]
    public void WhenOlderPollCompletesAfterNewerPoll_ThenOlderPollIsDropped()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyPoll(Reading(SpotifyPlaying(), new RenderingControlChange(50, null, null, null, null, null, null)), T0.AddSeconds(2));

        // Act
        player.ApplyPoll(
            new SonosPlayerReading(
                new AvTransportChange("PAUSED_PLAYBACK", null, null, null, null, null),
                TimeSpan.FromSeconds(7),
                null,
                new RenderingControlChange(10, null, null, null, null, null, null)),
            T0.AddSeconds(1));

        // Assert
        Assert.Equal(0.5m, player.Volume);
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
        Assert.Equal(TimeSpan.FromSeconds(42), player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenPollStartsLongBeforeTheLastPoll_ThenItIsTakenAsAClockJumpAndApplied()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyPoll(Reading(SpotifyPlaying(), new RenderingControlChange(50, null, null, null, null, null, null)), T0.AddMinutes(10));

        // Act
        player.ApplyPoll(Reading(SpotifyPlaying(), new RenderingControlChange(10, null, null, null, null, null, null)), T0);
        player.ApplyPoll(Reading(SpotifyPlaying(), new RenderingControlChange(20, null, null, null, null, null, null)), T0.AddSeconds(30));

        // Assert
        Assert.Equal(0.2m, player.Volume);
    }

    [Fact]
    public void WhenAvTransportEventApplied_ThenTrackStateUpdates()
    {
        // Arrange
        var player = CreateKitchen();

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
        var player = CreateKitchen();
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange("PLAYING", "SHUFFLE_NOREPEAT", SpotifyUri, SpotifyUri, "NOT_IMPLEMENTED", "NOT_IMPLEMENTED")), T0.AddSeconds(1));

        // Assert
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal(TimeSpan.FromSeconds(205), player.CurrentTrackDuration);
        Assert.Equal(TimeSpan.FromSeconds(42), player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenEventReportsNotImplementedTransportState_ThenTransportStateIsKept()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyAvTransportEvent(new AvTransportChange("NOT_IMPLEMENTED", null, null, null, null, null), T0.AddSeconds(1));

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
    }

    [Fact]
    public void WhenPollReportsNotImplementedTransportState_ThenTransportStateIsKept()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange("NOT_IMPLEMENTED", null, null, null, null, null)), T0.AddSeconds(1));

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
    }

    [Fact]
    public void WhenSameMetadataArrivesAfterIpChange_ThenImageUriFollowsTheNewAddress()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        var household = SonosSystemTopologyTests.ReadHousehold();
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
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0.AddSeconds(1));

        // Assert
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal("http://10.0.0.199:1400/getaa?s=1&u=x", player.CurrentTrackImageUri);
    }

    [Fact]
    public void WhenPollStartedBeforeEvent_ThenPollDoesNotOverwriteTheEvent()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0.AddSeconds(1));

        // Act
        player.ApplyPoll(
            Reading(new AvTransportChange("STOPPED", "NORMAL", "", "", "", ""), new RenderingControlChange(10, false, 0, 0, true, null, null)),
            T0);

        // Assert
        Assert.Equal(SonosTransportState.Playing, player.TransportState);
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal(0.1m, player.Volume);
        Assert.Equal(TimeSpan.FromSeconds(42), player.CurrentTrackPosition);
    }

    [Fact]
    public void WhenPollStartedAfterEvent_ThenPollApplies()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyAvTransportEvent(SpotifyPlaying(), T0);

        // Act
        player.ApplyPoll(Reading(new AvTransportChange("STOPPED", "NORMAL", "", "", "0:00:00", "")), T0.AddSeconds(1));

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
        var player = CreateKitchen();

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
    public void WhenRenderingControlPollStartedBeforeEvent_ThenVolumeFromEventIsKept()
    {
        // Arrange
        var player = CreateKitchen();
        player.ApplyRenderingControlEvent(new RenderingControlChange(44, null, null, null, null, null, null), T0.AddSeconds(1));

        // Act
        player.ApplyPoll(Reading(new AvTransportChange(null, null, null, null, null, null), new RenderingControlChange(10, false, 0, 0, true, null, null)), T0);

        // Assert
        Assert.Equal(0.44m, player.Volume);
    }

    [Fact]
    public void WhenTvIsPlaying_ThenSourceIsTv()
    {
        // Arrange
        var player = CreateKitchen();
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
        var player = CreateKitchen();

        // Act
        player.ApplyPoll(new SonosPlayerReading(
            new AvTransportChange(null, null, null, null, null, null),
            null,
            TimeSpan.FromMinutes(30),
            new RenderingControlChange(null, null, null, null, null, null, null)), T0);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(30), player.SleepTimerRemaining);
    }
}
