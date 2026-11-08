using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Namotion.Devices.Philips.Hue.HomeBlaze;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.Philips.Hue.Tests.Widgets;

/// <summary>
/// The widget leaves out the subjects below the device that the viewer may not see.
/// </summary>
public sealed class HueBridgeWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public HueBridgeWidgetAccessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private static TSubject Attach<TSubject>(TSubject subject)
        where TSubject : IInterceptorSubject
    {
        subject.Context.AddFallbackContext(InterceptorSubjectContext.Create().WithRegistry());
        return subject;
    }

    private static string[] GetValues(IRenderedComponent<HueBridgeWidgetComponent> rendered) =>
        rendered.FindAll(".mud-typography-h5").Select(value => value.TextContent.Trim()).ToArray();

    [Fact]
    public void WhenALightIsHidden_ThenTheWidgetDoesNotCountIt()
    {
        // Arrange
        var bridge = Attach(TestHelpers.CreateTestBridge());
        bridge.Lights = new Dictionary<string, HueLightbulb>
        {
            ["shown"] = TestHelpers.CreateLightbulb("LCT001", isOn: true),
            ["hidden"] = TestHelpers.CreateLightbulb("LCT001", isOn: false)
        };
        _permissions.CannotShow.Add(bridge.Lights["hidden"]);

        // Act
        var rendered = Render<HueBridgeWidgetComponent>(parameters => parameters.Add(widget => widget.Subject, bridge));

        // Assert
        Assert.Equal(["1", "0", "0", "0"], GetValues(rendered));
    }

    [Fact]
    public void WhenALightIsHidden_ThenTheEditComponentDoesNotCountIt()
    {
        // Arrange
        var bridge = Attach(TestHelpers.CreateTestBridge());
        bridge.Lights = new Dictionary<string, HueLightbulb>
        {
            ["shown"] = TestHelpers.CreateLightbulb("LCT001", isOn: true),
            ["hidden"] = TestHelpers.CreateLightbulb("LCT001", isOn: false)
        };
        _permissions.CannotShow.Add(bridge.Lights["hidden"]);

        // Act
        var rendered = Render<HueBridgeEditComponent>(parameters => parameters.Add(component => component.Subject, bridge));

        // Assert
        Assert.Contains("Lights: 1", rendered.Markup, StringComparison.Ordinal);
    }
}
