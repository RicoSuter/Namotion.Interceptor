using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Controls;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Samples;
using HomeBlaze.Samples.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Interceptor.Registry;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>The widget of the sample motor, which plugin authors copy, changes its target speed only through the <see cref="PropertyWriter"/>.</summary>
public sealed class MotorWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly PersistenceTestGraph _graph = new();
    private readonly TestUserPermissions _permissions = new();
    private readonly Motor _motor;

    public MotorWidgetAccessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IUserPermissions>(_permissions);
        Services.AddSingleton(_graph.RootManager);
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<PropertyWriter>();
        _motor = new Motor(_graph.Context) { TargetSpeed = 1000 };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        _graph.Dispose();
    }

    [Fact]
    public async Task WhenTheViewerMayNotAdministerTheTargetSpeed_ThenTheSliderIsDisabledAndAChangeIsRefused()
    {
        // Arrange
        _permissions.CannotAdminister.Add(_motor.TryGetRegisteredSubject()!.TryGetProperty(nameof(Motor.TargetSpeed))!);
        var widget = RenderWidget();
        var slider = widget.FindComponent<MudSlider<int>>();

        // Act
        await widget.InvokeAsync(() => slider.Instance.ValueChanged.InvokeAsync(2000));

        // Assert
        Assert.True(slider.Instance.Disabled);
        Assert.Equal(1000, _motor.TargetSpeed);
    }

    [Fact]
    public async Task WhenTheViewerMayAdministerTheTargetSpeed_ThenTheSliderChangesIt()
    {
        // Arrange
        var widget = RenderWidget();
        var slider = widget.FindComponent<MudSlider<int>>();

        // Act
        await widget.InvokeAsync(() => slider.Instance.ValueChanged.InvokeAsync(2000));

        // Assert
        Assert.False(slider.Instance.Disabled);
        Assert.Equal(2000, _motor.TargetSpeed);
    }

    private IRenderedComponent<MotorWidgetComponent> RenderWidget() =>
        Render<MotorWidgetComponent>(parameters => parameters
            .Add(component => component.Subject, _motor)
            .AddCascadingValue("IsEditing", true));
}
