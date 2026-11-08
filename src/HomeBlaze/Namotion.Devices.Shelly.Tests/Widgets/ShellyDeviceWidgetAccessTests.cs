using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Namotion.Devices.Shelly.HomeBlaze;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.Shelly.Tests.Widgets;

/// <summary>
/// The widget leaves out the subjects below the device that the viewer may not see.
/// </summary>
public sealed class ShellyDeviceWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public ShellyDeviceWidgetAccessTests()
    {
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

    private static string[] GetValues(IRenderedComponent<ShellyDeviceWidget> rendered) =>
        rendered.FindAll(".mud-typography-h5").Select(value => value.TextContent.Trim()).ToArray();

    [Fact]
    public void WhenTheComponentsOfTheDeviceAreHidden_ThenTheWidgetNeitherCountsNorReadsThem()
    {
        // Arrange
        var device = Attach(new ShellyDevice(new TestHttpClientFactory(), NullLogger<ShellyDevice>.Instance));
        device.Switches = [new ShellySwitch(device, 0), new ShellySwitch(device, 1)];
        device.Covers = [new ShellyCover(device, 0)];
        device.Inputs = [new ShellyInput(0), new ShellyInput(1)];
        device.EnergyMeter = new ShellyEnergyMeter { MeasuredPower = 4321m };
        var shownMarkup = Render<ShellyDeviceWidget>(parameters => parameters.Add(widget => widget.Subject, device)).Markup;
        _permissions.CannotShow.Add(device.Switches[1]);
        _permissions.CannotShow.Add(device.Covers[0]);
        _permissions.CannotShow.Add(device.Inputs[0]);
        _permissions.CannotShow.Add(device.EnergyMeter);

        // Act
        var rendered = Render<ShellyDeviceWidget>(parameters => parameters.Add(widget => widget.Subject, device));

        // Assert
        Assert.Contains("4321", shownMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("4321", rendered.Markup, StringComparison.Ordinal);
        Assert.Equal(["1", "1"], GetValues(rendered));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
