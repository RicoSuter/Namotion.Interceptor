using System.ComponentModel;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// Derived state must raise change notifications, otherwise it renders stale and is never recorded to history.
/// </summary>
public class SonosDerivedTrackingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenTransportStateChanges_ThenIsPlayingRaisesPropertyChanged()
    {
        // Arrange
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
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
        var system = SonosSystemTopologyTests.CreateSystem();
        system.ApplyTopology(SonosSystemTopologyTests.ReadHousehold());
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
        var system = Track(SonosPlayerOperationTests.CreateGroupedSystem());
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
