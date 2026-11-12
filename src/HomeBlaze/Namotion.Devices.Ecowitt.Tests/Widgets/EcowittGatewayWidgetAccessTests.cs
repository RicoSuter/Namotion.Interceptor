using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Namotion.Devices.Ecowitt.HomeBlaze;
using Namotion.Devices.Ecowitt.Sensors;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests.Widgets;

/// <summary>
/// The widget leaves out the subjects below the device that the viewer may not see.
/// </summary>
public sealed class EcowittGatewayWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public EcowittGatewayWidgetAccessTests()
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

    private static string[] GetValues(IRenderedComponent<EcowittGatewayWidget> rendered) =>
        rendered.FindAll(".mud-typography-h5").Select(value => value.TextContent.Trim()).ToArray();

    [Fact]
    public void WhenTheSensorsAreHidden_ThenTheWidgetShowsNoneOfTheirValues()
    {
        // Arrange
        var gateway = Attach(new EcowittGateway(new TestHttpClientFactory(), NullLogger<EcowittGateway>.Instance));
        gateway.OutdoorSensor = new EcowittOutdoorSensor { Temperature = 21.5m, Humidity = 0.43m, WindSpeed = 3.4m };
        gateway.IndoorSensor = new EcowittIndoorSensor { Temperature = 19.3m };
        gateway.RainGauge = new EcowittRainGauge("rain") { RainRate = 2.7m };
        gateway.ChannelSensors = [new EcowittChannelSensor(1), new EcowittChannelSensor(2)];
        var shown = Render<EcowittGatewayWidget>(parameters => parameters.Add(widget => widget.Subject, gateway));
        _permissions.CannotShow.Add(gateway.OutdoorSensor);
        _permissions.CannotShow.Add(gateway.IndoorSensor);
        _permissions.CannotShow.Add(gateway.RainGauge);
        _permissions.CannotShow.Add(gateway.ChannelSensors[1]);

        // Act
        var rendered = Render<EcowittGatewayWidget>(parameters => parameters.Add(widget => widget.Subject, gateway));

        // Assert
        Assert.Equal(6, GetValues(shown).Length);
        Assert.Equal(["1"], GetValues(rendered));
    }

    [Fact]
    public void WhenASensorIsHidden_ThenTheEditComponentDoesNotListIt()
    {
        // Arrange
        var gateway = Attach(new EcowittGateway(new TestHttpClientFactory(), NullLogger<EcowittGateway>.Instance));
        gateway.ChannelSensors = [new EcowittChannelSensor(1), new EcowittChannelSensor(2)];
        _permissions.CannotShow.Add(gateway.ChannelSensors[1]);

        // Act
        var rendered = Render<EcowittGatewayEditComponent>(parameters => parameters.Add(component => component.Subject, gateway));

        // Assert
        Assert.Contains("ch:1", rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ch:2", rendered.Markup, StringComparison.Ordinal);
    }
}
