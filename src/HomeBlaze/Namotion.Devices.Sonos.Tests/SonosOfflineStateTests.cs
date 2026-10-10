using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// While a player is not connected its playback state is unknown, so it never keeps reporting that it plays. The
/// last known track stays.
/// </summary>
public class SonosOfflineStateTests
{
    private static AvTransportChange Playing() => new(
        "PLAYING",
        "NORMAL",
        SpotifyConnectUri,
        SpotifyConnectUri,
        "0:03:25",
        SonosEventBodies.Didl("Song", "Artist", "Album"));

    [Fact]
    public void WhenPlayingPlayerStopsAnswering_ThenPlaybackIsUnknownAndTheLastTrackIsKept()
    {
        // Arrange
        var player = CreateReachableHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(Playing(), T0);
        Assert.True(player.IsPlaying);

        // Act
        player.ReportPollFailed("The speaker does not answer.");

        // Assert
        Assert.Null(player.PlaybackState);
        Assert.False(player.IsPlaying);
        Assert.Equal("Speaker", player.IconName);
        Assert.Equal("Song", player.CurrentTrackTitle);
        Assert.Equal("Artist", player.CurrentTrackArtist);
        Assert.Equal(SpotifyConnectUri, player.CurrentTrackUri);
        Assert.Equal(TimeSpan.FromSeconds(205), player.CurrentTrackDuration);
        Assert.Equal(SonosSource.SpotifyConnect, player.Source);
    }

    [Fact]
    public void WhenPlayingPlayerLeavesTheTopology_ThenPlaybackIsUnknown()
    {
        // Arrange
        var player = CreateReachableHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(Playing(), T0);

        // Act
        player.MarkMissing();

        // Assert
        Assert.Null(player.PlaybackState);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void WhenOfflinePlayerAnswersAgain_ThenItsPlaybackStateIsReportedAgain()
    {
        // Arrange
        var player = CreateReachableHousehold().Players[KitchenUuid];
        player.ApplyAvTransportEvent(Playing(), T0);
        player.ReportPollFailed("The speaker does not answer.");

        // Act
        player.ReportPollSucceeded();

        // Assert
        Assert.Equal(MediaPlaybackState.Playing, player.PlaybackState);
        Assert.True(player.IsPlaying);
    }

    [Fact]
    public void WhenPlayerWasNeverPolled_ThenAnEventDoesNotMakeItReportPlaying()
    {
        // Arrange
        var player = CreateHousehold().Players[KitchenUuid];

        // Act
        player.ApplyAvTransportEvent(Playing(), T0);

        // Assert
        Assert.Null(player.PlaybackState);
        Assert.False(player.IsPlaying);
        Assert.Equal("Song", player.CurrentTrackTitle);
    }

    [Fact]
    public void WhenCoordinatorStopsAnswering_ThenItsGroupAndMembersReportUnknownPlayback()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var coordinator = system.Players[OfficeUuid];
        var member = system.Players[KitchenUuid];
        var group = system.Groups[OfficeUuid];
        coordinator.ApplyAvTransportEvent(Playing(), T0);
        Assert.True(group.IsPlaying);
        Assert.True(member.IsPlaying);

        // Act
        coordinator.ReportPollFailed("The speaker does not answer.");

        // Assert
        Assert.Null(group.PlaybackState);
        Assert.False(group.IsPlaying);
        Assert.Equal("Song", group.CurrentTrackTitle);
        Assert.Null(member.PlaybackState);
        Assert.False(member.IsPlaying);
        Assert.Equal("Song", member.CurrentTrackTitle);
    }

    [Fact]
    public void WhenMemberStopsAnswering_ThenOnlyTheMemberReportsUnknownPlayback()
    {
        // Arrange
        var system = CreateGroupedSystem();
        var coordinator = system.Players[OfficeUuid];
        var member = system.Players[KitchenUuid];
        coordinator.ApplyAvTransportEvent(Playing(), T0);

        // Act
        member.ReportPollFailed("The speaker does not answer.");

        // Assert
        Assert.Null(member.PlaybackState);
        Assert.False(member.IsPlaying);
        Assert.Equal(MediaPlaybackState.Playing, coordinator.PlaybackState);
        Assert.True(system.Groups[OfficeUuid].IsPlaying);
    }
}
