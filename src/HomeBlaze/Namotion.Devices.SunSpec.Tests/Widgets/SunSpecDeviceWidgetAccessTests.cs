using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Namotion.Devices.SunSpec.HomeBlaze;
using Namotion.Devices.SunSpec.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.SunSpec.Tests.Widgets;

/// <summary>
/// The widget leaves out the subjects below the device that the viewer may not see.
/// </summary>
public sealed class SunSpecDeviceWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public SunSpecDeviceWidgetAccessTests()
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

    private static string[] GetValues(IRenderedComponent<SunSpecDeviceWidget> rendered) =>
        rendered.FindAll(".mud-typography-h5").Select(value => value.TextContent.Trim()).ToArray();

    [Fact]
    public void WhenTheModelOfALogicalDeviceIsHidden_ThenTheWidgetDoesNotShowItsValue()
    {
        // Arrange
        var device = Attach(new SunSpecDevice(NullLogger<SunSpecDevice>.Instance));
        var meter = new SunSpecAcMeter(203, 40188, 105) { W = 4321m };
        var logicalDevice = new SunSpecLogicalDevice(1, 0) { Models = [meter] };
        device.Units = new Dictionary<int, SunSpecUnit> { [1] = new(1) { Devices = [logicalDevice] } };
        var shownMarkup = Render<SunSpecDeviceWidget>(parameters => parameters.Add(widget => widget.Subject, device)).Markup;
        _permissions.CannotShow.Add(meter);

        // Act
        var rendered = Render<SunSpecDeviceWidget>(parameters => parameters.Add(widget => widget.Subject, device));

        // Assert
        Assert.Contains("4321", shownMarkup, StringComparison.Ordinal);
        Assert.Contains(logicalDevice.Title, rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", rendered.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenALogicalDeviceIsHidden_ThenTheEditComponentDoesNotListIt()
    {
        // Arrange
        var device = Attach(new SunSpecDevice(NullLogger<SunSpecDevice>.Instance));
        var shownDevice = new SunSpecLogicalDevice(1, 0);
        var hiddenDevice = new SunSpecLogicalDevice(1, 1);
        device.Units = new Dictionary<int, SunSpecUnit> { [1] = new(1) { Devices = [shownDevice, hiddenDevice] } };
        _permissions.CannotShow.Add(hiddenDevice);

        // Act
        var rendered = Render<SunSpecDeviceEditComponent>(parameters => parameters.Add(component => component.Subject, device));

        // Assert
        Assert.Contains(shownDevice.Title, rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(hiddenDevice.Title, rendered.Markup, StringComparison.Ordinal);
    }
}
