using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Namotion.Devices.Gpio.HomeBlaze;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.Gpio.Tests.Widgets;

/// <summary>
/// The widget leaves out the subjects below the device that the viewer may not see.
/// </summary>
public sealed class GpioSubjectWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public GpioSubjectWidgetAccessTests()
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

    private static string[] GetValues(IRenderedComponent<GpioSubjectWidget> rendered) =>
        rendered.FindAll(".mud-typography-h5").Select(value => value.TextContent.Trim()).ToArray();

    [Fact]
    public void WhenPinsAndChannelsAreHidden_ThenTheWidgetDoesNotCountThem()
    {
        // Arrange
        var gpio = Attach(new GpioSubject());
        gpio.Pins = new Dictionary<int, GpioPin>
        {
            [1] = new() { Mode = GpioPinMode.Input },
            [2] = new() { Mode = GpioPinMode.Output },
            [3] = new() { Mode = GpioPinMode.Output }
        };
        gpio.AnalogChannels = new Dictionary<int, AnalogChannel> { [0] = new() };
        var shownMarkup = Render<GpioSubjectWidget>(parameters => parameters.Add(widget => widget.Subject, gpio)).Markup;
        _permissions.CannotShow.Add(gpio.Pins[1]);
        _permissions.CannotShow.Add(gpio.Pins[3]);
        _permissions.CannotShow.Add(gpio.AnalogChannels[0]);

        // Act
        var rendered = Render<GpioSubjectWidget>(parameters => parameters.Add(widget => widget.Subject, gpio));

        // Assert
        Assert.Contains("Analog Channels", shownMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("Analog Channels", rendered.Markup, StringComparison.Ordinal);
        Assert.Equal(["1", "0", "1"], GetValues(rendered));
    }
}
