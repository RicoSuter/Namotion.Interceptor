using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.HomeBlaze;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.Luxtronik.Tests.Widgets;

/// <summary>
/// The widget leaves out the subjects below the device that the viewer may not see.
/// </summary>
public sealed class LuxtronikHeatPumpWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public LuxtronikHeatPumpWidgetAccessTests()
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

    private static string[] GetValues(IRenderedComponent<LuxtronikHeatPumpWidget> rendered) =>
        rendered.FindAll(".mud-typography-h5").Select(value => value.TextContent.Trim()).ToArray();

    [Fact]
    public void WhenTheSubjectsBelowTheHeatPumpAreHidden_ThenTheWidgetShowsNoneOfTheirValues()
    {
        // Arrange
        var heatPump = Attach(new LuxtronikHeatPump(NullLogger<LuxtronikHeatPump>.Instance));
        heatPump.OperatingStatus.OperatingState = LuxtronikOperatingState.Defrost;
        heatPump.Temperatures.Outside.Temperature = 7.5m;
        heatPump.Temperatures.Flow.Temperature = 34.5m;
        heatPump.HotWater.Temperature.Temperature = 48.5m;
        heatPump.Energy.ElectricalPower = 1234m;
        heatPump.Energy.ThermalPower = 5678m;
        var shownMarkup = Render<LuxtronikHeatPumpWidget>(parameters => parameters.Add(widget => widget.Subject, heatPump)).Markup;
        _permissions.CannotShow.Add(heatPump.OperatingStatus);
        _permissions.CannotShow.Add(heatPump.Temperatures.Outside);
        _permissions.CannotShow.Add(heatPump.Temperatures.Flow);
        _permissions.CannotShow.Add(heatPump.HotWater.Temperature);
        _permissions.CannotShow.Add(heatPump.Energy);

        // Act
        var rendered = Render<LuxtronikHeatPumpWidget>(parameters => parameters.Add(widget => widget.Subject, heatPump));

        // Assert
        Assert.Contains("Defrost", shownMarkup, StringComparison.Ordinal);
        Assert.Equal(["-", "-", "-", "-", "-", "-"], GetValues(rendered));
    }
}
