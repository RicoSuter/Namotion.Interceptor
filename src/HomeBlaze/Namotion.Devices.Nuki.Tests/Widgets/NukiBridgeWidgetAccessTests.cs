using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Namotion.Devices.Nuki.HomeBlaze;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Xunit;

namespace Namotion.Devices.Nuki.Tests.Widgets;

/// <summary>
/// The widget leaves out the subjects below the device that the viewer may not see.
/// </summary>
public sealed class NukiBridgeWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private readonly TestUserPermissions _permissions = new();

    public NukiBridgeWidgetAccessTests()
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

    private static string[] GetValues(IRenderedComponent<NukiBridgeWidget> rendered) =>
        rendered.FindAll(".mud-typography-h5").Select(value => value.TextContent.Trim()).ToArray();

    [Fact]
    public void WhenALockAndAnOpenerAreHidden_ThenTheWidgetDoesNotCountThem()
    {
        // Arrange
        var bridge = Attach(new NukiBridge(NullLogger<NukiBridge>.Instance));
        bridge.Locks = new Dictionary<string, NukiSmartLock>
        {
            ["1"] = new(bridge, 1, NukiDeviceType.SmartLock),
            ["2"] = new(bridge, 2, NukiDeviceType.SmartLock)
        };
        bridge.Openers = new Dictionary<string, NukiOpener> { ["3"] = new(bridge, 3) };
        _permissions.CannotShow.Add(bridge.Locks["2"]);
        _permissions.CannotShow.Add(bridge.Openers["3"]);

        // Act
        var rendered = Render<NukiBridgeWidget>(parameters => parameters.Add(widget => widget.Subject, bridge));

        // Assert
        Assert.Equal(["1", "0"], GetValues(rendered));
    }
}
