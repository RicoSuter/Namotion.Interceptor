using System.ComponentModel;
using HueApi.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

/// <summary>
/// The [Derived] [State] properties of the Hue subjects are computed from the raw Hue API resources
/// the bridge assigns on every poll. Replacing a resource must recalculate them and raise a change,
/// otherwise nothing observing the graph (the history stores, the UI) ever sees the new value.
/// </summary>
public class HueDerivedStateTrackingTests
{
    [Fact]
    public void WhenGroupedLightIsReplaced_ThenIsOnRaisesPropertyChanged()
    {
        // Arrange
        var group = Track(new HueGroup(
            TestHelpers.CreateRoom(),
            TestHelpers.CreateGroupedLight(isOn: false),
            [],
            null!));

        Assert.False(group.IsOn);
        var firedEvents = TrackPropertyChanged(group);

        // Act
        group.GroupedLight = TestHelpers.CreateGroupedLight(isOn: true);

        // Assert
        Assert.True(group.IsOn);
        Assert.Contains(nameof(HueGroup.IsOn), firedEvents);
    }

    [Fact]
    public void WhenLightResourceIsReplaced_ThenIsOnRaisesPropertyChanged()
    {
        // Arrange
        var lightbulb = Track(TestHelpers.CreateLightbulb("TEST001", isOn: false));

        Assert.False(lightbulb.IsOn);
        var firedEvents = TrackPropertyChanged(lightbulb);

        // Act
        lightbulb.LightResource = TestHelpers.CreateLight(isOn: true);

        // Assert
        Assert.True(lightbulb.IsOn);
        Assert.Contains(nameof(HueLightbulb.IsOn), firedEvents);
    }

    [Fact]
    public void WhenZigbeeConnectivityIsReplaced_ThenIsConnectedRaisesPropertyChanged()
    {
        // Arrange
        var device = Track(TestHelpers.CreateHueDevice(ConnectivityStatus.connected));

        Assert.True(device.IsConnected);
        var firedEvents = TrackPropertyChanged(device);

        // Act
        device.ZigbeeConnectivity = TestHelpers.CreateZigbeeConnectivity(ConnectivityStatus.connectivity_issue);

        // Assert
        Assert.False(device.IsConnected);
        Assert.Contains(nameof(HueDevice.IsConnected), firedEvents);
    }

    /// <summary>
    /// Attaches the subject to a context with derived-property tracking, the way the HomeBlaze graph
    /// does when the bridge publishes its children.
    /// </summary>
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
