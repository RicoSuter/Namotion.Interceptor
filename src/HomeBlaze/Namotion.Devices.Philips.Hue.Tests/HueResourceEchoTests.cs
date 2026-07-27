using System.ComponentModel;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests;

/// <summary>
/// After the bridge accepts a command, the operation echoes the result locally so the UI does not wait
/// for the next poll. The echo has to reach interception to be worth anything, which is what these
/// cover: mutating the resource in place changed no intercepted property, so nothing recomputed the
/// derived state, nothing notified and no history sample was recorded.
/// </summary>
public class HueResourceEchoTests
{
    [Fact]
    public void WhenAResourceIsEchoed_ThenTheCopyCarriesTheChangeAndTheOriginalDoesNot()
    {
        // Arrange
        var light = TestHelpers.CreateLight(isOn: false, brightness: 42d);

        // Act
        var updated = HueResourceEcho.With(light, copy => copy.On.IsOn = true);

        // Assert - a distinct instance, or the equality check would veto the write as unchanged.
        Assert.NotSame(light, updated);
        Assert.True(updated.On.IsOn);
        Assert.False(light.On.IsOn);
    }

    [Fact]
    public void WhenAResourceIsEchoed_ThenTheRestOfItSurvivesTheCopy()
    {
        // Arrange
        var light = TestHelpers.CreateLight(isOn: true, brightness: 42d, mirek: 300);

        // Act
        var updated = HueResourceEcho.With(light, copy => copy.On.IsOn = false);

        // Assert - the copy replaces the resource wholesale, so anything it loses is lost from the model.
        Assert.Equal(light.Id, updated.Id);
        Assert.Equal(light.Type, updated.Type);
        Assert.Equal(light.Dimming!.Brightness, updated.Dimming!.Brightness);
        Assert.Equal(light.ColorTemperature!.Mirek, updated.ColorTemperature!.Mirek);
        Assert.Equal(
            light.ColorTemperature.MirekSchema.MirekMaximum, updated.ColorTemperature.MirekSchema.MirekMaximum);
    }

    [Fact]
    public void WhenTheEchoedResourceIsAssigned_ThenIsOnRaisesPropertyChanged()
    {
        // Arrange
        var lightbulb = Track(TestHelpers.CreateLightbulb("LCT001", isOn: false));
        var firedEvents = new List<string>();
        ((INotifyPropertyChanged)lightbulb).PropertyChanged += (_, args) => firedEvents.Add(args.PropertyName!);

        // Act - the assignment an [Operation] performs after the bridge confirms the command.
        lightbulb.LightResource = HueResourceEcho.With(lightbulb.LightResource, light => light.On.IsOn = true);

        // Assert
        Assert.True(lightbulb.IsOn);
        Assert.Contains(nameof(HueLightbulb.IsOn), firedEvents);
    }

    private static TSubject Track<TSubject>(TSubject subject)
        where TSubject : IInterceptorSubject
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        subject.Context.AddFallbackContext(context);
        return subject;
    }
}
