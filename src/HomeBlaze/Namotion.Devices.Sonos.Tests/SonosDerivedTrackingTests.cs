using System.ComponentModel;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// Derived state must raise change notifications, otherwise it renders stale and is never recorded to history.
/// </summary>
public class SonosDerivedTrackingTests
{

    [Fact]
    public void WhenPlaybackStateChanges_ThenIsPlayingRaisesPropertyChanged()
    {
        // Arrange
        var system = CreateReachableHousehold();
        var player = Track(system.Players[TestFixtures.KitchenUuid]);
        var firedEvents = TrackPropertyChanged(player);

        // Act
        player.ApplyAvTransportEvent(new AvTransportChange("PLAYING", null, null, null, null, null), T0);

        // Assert
        Assert.True(player.IsPlaying);
        Assert.Contains(nameof(SonosPlayer.IsPlaying), firedEvents);
    }

    [Fact]
    public void WhenCoordinatorTrackChanges_ThenGroupTitleOfTrackRaisesPropertyChanged()
    {
        // Arrange
        var system = CreateHousehold();
        var coordinator = Track(system.Players[TestFixtures.LivingRoomUuid]);
        var group = Track(system.Groups[TestFixtures.LivingRoomUuid]);
        Assert.Null(group.CurrentTrackTitle);
        var firedEvents = TrackPropertyChanged(group);

        // Act
        coordinator.ApplyAvTransportEvent(new AvTransportChange(null, null, null, null, null, SonosEventBodies.Didl("Song")), T0);

        // Assert
        Assert.Equal("Song", group.CurrentTrackTitle);
        Assert.Contains(nameof(SonosGroup.CurrentTrackTitle), firedEvents);
    }

    [Fact]
    public void WhenCoordinatorGoesOffline_ThenMemberPlayIsEnabledRaisesPropertyChanged()
    {
        // Arrange
        var system = Track(CreateGroupedSystem());
        var coordinator = Track(system.Players[TestFixtures.OfficeUuid]);
        var member = Track(system.Players[TestFixtures.KitchenUuid]);
        Assert.True(member.Play_IsEnabled);
        var firedEvents = TrackPropertyChanged(member);

        // Act
        coordinator.ReportPollFailed("The speaker does not answer.");

        // Assert
        Assert.False(member.Play_IsEnabled);
        Assert.Contains(nameof(SonosPlayer.Play_IsEnabled), firedEvents);
        Assert.DoesNotContain(nameof(SonosPlayer.SetVolume_IsEnabled), firedEvents);
    }

    [Fact]
    public void WhenCoordinatorPlayModeChanges_ThenMemberShuffleRaisesPropertyChanged()
    {
        // Arrange
        var system = Track(CreateGroupedSystem());
        var coordinator = Track(system.Players[TestFixtures.OfficeUuid]);
        var member = Track(system.Players[TestFixtures.KitchenUuid]);
        Assert.Null(member.Shuffle);
        var firedEvents = TrackPropertyChanged(member);

        // Act
        coordinator.ApplyAvTransportEvent(new AvTransportChange(null, "SHUFFLE_NOREPEAT", null, null, null, null), T0);

        // Assert
        Assert.True(member.Shuffle);
        Assert.Contains(nameof(SonosPlayer.Shuffle), firedEvents);
    }

    [Fact]
    public void WhenPlayingPlayerGoesOffline_ThenPlaybackStateAndIsPlayingRaisePropertyChanged()
    {
        // Arrange
        var system = Track(CreateReachableHousehold());
        var player = Track(system.Players[TestFixtures.KitchenUuid]);
        var group = Track(system.Groups[TestFixtures.KitchenUuid]);
        player.ApplyAvTransportEvent(new AvTransportChange("PLAYING", null, null, null, null, null), T0);
        Assert.True(player.IsPlaying);
        Assert.True(group.IsPlaying);
        var firedEvents = TrackPropertyChanged(player);
        var firedGroupEvents = TrackPropertyChanged(group);

        // Act
        player.ReportPollFailed("The speaker does not answer.");

        // Assert
        Assert.False(player.IsPlaying);
        Assert.Contains(nameof(SonosPlayer.PlaybackState), firedEvents);
        Assert.Contains(nameof(SonosPlayer.IsPlaying), firedEvents);
        Assert.Contains(nameof(SonosPlayer.IconName), firedEvents);
        Assert.False(group.IsPlaying);
        Assert.Contains(nameof(SonosGroup.IsPlaying), firedGroupEvents);
    }

    [Fact]
    public void WhenCoordinatorChangesTrack_ThenMemberTrackAndPlaybackRaisePropertyChanged()
    {
        // Arrange
        var system = Track(CreateGroupedSystem());
        var coordinator = Track(system.Players[TestFixtures.OfficeUuid]);
        var member = Track(system.Players[TestFixtures.KitchenUuid]);
        coordinator.ApplyAvTransportEvent(
            new AvTransportChange("PAUSED_PLAYBACK", null, "x-rincon-queue:q#0", "x-file-cifs://nas/a.mp3", "0:03:00", SonosEventBodies.Didl("Song A", "Artist A")),
            T0);
        Assert.Equal("Song A", member.CurrentTrackTitle);
        var firedEvents = TrackPropertyChanged(member);

        // Act
        coordinator.ApplyAvTransportEvent(
            new AvTransportChange("PLAYING", null, "x-rincon-queue:q#0", "x-file-cifs://nas/b.mp3", "0:04:00", SonosEventBodies.Didl("Song B", "Artist B")),
            T0 + 1);

        // Assert
        Assert.Equal("Song B", member.CurrentTrackTitle);
        Assert.Equal("Artist B", member.CurrentTrackArtist);
        Assert.True(member.IsPlaying);
        Assert.Contains(nameof(SonosPlayer.CurrentTrackTitle), firedEvents);
        Assert.Contains(nameof(SonosPlayer.CurrentTrackArtist), firedEvents);
        Assert.Contains(nameof(SonosPlayer.CurrentTrackUri), firedEvents);
        Assert.Contains(nameof(SonosPlayer.CurrentTrackDuration), firedEvents);
        Assert.Contains(nameof(SonosPlayer.PlaybackState), firedEvents);
        Assert.Contains(nameof(SonosPlayer.IsPlaying), firedEvents);
    }

    [Fact]
    public void WhenMemberLeavesTheGroup_ThenMemberTrackRaisesPropertyChanged()
    {
        // Arrange
        var system = Track(CreateGroupedSystem());
        var coordinator = Track(system.Players[TestFixtures.OfficeUuid]);
        var member = Track(system.Players[TestFixtures.KitchenUuid]);
        coordinator.ApplyAvTransportEvent(
            new AvTransportChange("PLAYING", null, "x-rincon-queue:q#0", "x-file-cifs://nas/a.mp3", "0:03:00", SonosEventBodies.Didl("Song A")),
            T0);
        Assert.Equal("Song A", member.CurrentTrackTitle);
        var firedEvents = TrackPropertyChanged(member);

        // Act
        system.ApplyTopology(ZoneGroupStateParser.Parse(SonosEventBodies.CreateStandaloneTopology(
            (TestFixtures.OfficeUuid, "Büro", new Uri("http://10.0.0.116:1400/")),
            (TestFixtures.KitchenUuid, "Küche", new Uri("http://10.0.0.121:1400/")))));

        // Assert
        Assert.Null(member.CurrentTrackTitle);
        Assert.False(member.IsPlaying);
        Assert.Contains(nameof(SonosPlayer.CurrentTrackTitle), firedEvents);
        Assert.Contains(nameof(SonosPlayer.IsPlaying), firedEvents);
    }

    private static T Track<T>(T subject) where T : IInterceptorSubject
    {
        subject.Context.AddFallbackContext(InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry());

        return subject;
    }

    private static List<string> TrackPropertyChanged(INotifyPropertyChanged subject)
    {
        var firedEvents = new List<string>();
        subject.PropertyChanged += (_, arguments) => firedEvents.Add(arguments.PropertyName!);
        return firedEvents;
    }
}
