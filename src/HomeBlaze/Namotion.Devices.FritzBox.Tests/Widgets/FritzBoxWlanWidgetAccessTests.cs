using Bunit;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Tests.Access;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MudBlazor;
using MudBlazor.Services;
using Namotion.Devices.FritzBox.HomeBlaze;
using Namotion.Devices.FritzBox.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests.Widgets;

/// <summary>
/// The switch of the guest WLAN turns it on or off only when the viewer may invoke the operation.
/// </summary>
public sealed class FritzBoxWlanWidgetAccessTests : BunitContext, IAsyncLifetime
{
    private const string DeniedTooltip = "Your roles do not allow this.";

    private readonly TestUserPermissions _permissions = new();
    private readonly List<MessageBoxOptions> _shownMessages = [];
    private FakeFritzBox? _fake;
    private FritzBox? _router;
    private FritzBoxGuestWlan? _guest;

    public FritzBoxWlanWidgetAccessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);

        var dialogs = new Mock<IDialogService>();
        dialogs
            .Setup(service => service.ShowMessageBoxAsync(It.IsAny<MessageBoxOptions>(), It.IsAny<DialogOptions?>()))
            .ReturnsAsync((MessageBoxOptions options, DialogOptions? _) =>
            {
                _shownMessages.Add(options);
                return true;
            });
        Services.AddSingleton(dialogs.Object);
        Services.AddSingleton<IUserPermissions>(_permissions);
    }

    private FritzBoxGuestWlan Guest => _guest!;

    // The switch turns the guest WLAN to the other state, so it runs the operation of that state.
    private string SwitchMethod => Guest.IsOn == true ? nameof(FritzBoxGuestWlan.TurnOffAsync) : nameof(FritzBoxGuestWlan.TurnOnAsync);

    public async Task InitializeAsync()
    {
        _fake = await FakeFritzBox.StartAsync();
        _router = new FritzBox(NullLogger<FritzBox>.Instance)
        {
            HostAddress = "127.0.0.1",
            Username = _fake.Username,
            Password = _fake.Password,
            DescriptionPort = _fake.HttpPort,
            MinimumInterval = TimeSpan.FromMilliseconds(50),

            // The test switches the guest WLAN itself; a poll is not needed after the first.
            PollingInterval = TimeSpan.FromHours(1),
            RetryInterval = TimeSpan.FromMilliseconds(100)
        };

        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer);
        ((IInterceptorSubject)_router).Context.AddFallbackContext(context);

        await _router.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => _router.Wlans.TryGetValue(FritzBoxWlanRoles.Guest, out var wlan) && wlan.IsOn is not null,
            message: "The router should report the state of its guest WLAN.");
        _guest = (FritzBoxGuestWlan)_router.Wlans[FritzBoxWlanRoles.Guest];
    }

    // MudBlazor's services only dispose asynchronously, which xUnit 2 calls through IAsyncLifetime alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        if (_router is not null)
        {
            await _router.StopAsync(CancellationToken.None);
            _router.Dispose();
        }

        if (_fake is not null)
        {
            await _fake.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenTheUserMayInvokeTheSwitchOperation_ThenTheSwitchIsEnabledAndSwitchesTheGuestWlan()
    {
        // Arrange
        var wasOn = Guest.IsOn!.Value;
        var rendered = Render<FritzBoxWlanWidget>(parameters => parameters.Add(widget => widget.Subject, Guest));
        var input = FindSwitch(rendered);
        Assert.False(input.HasAttribute("disabled"), rendered.Markup);

        // Act
        await input.ChangeAsync(new() { Value = !wasOn });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => _fake!.Actions.Contains("SetEnable"), message: "The router should switch the guest WLAN.");
        Assert.Equal(!wasOn, _fake!.IsGuestWlanEnabled);
        Assert.Empty(_shownMessages);
    }

    [Fact]
    public void WhenTheUserMayNotInvokeTheSwitchOperation_ThenTheSwitchIsDisabledWithTheReason()
    {
        // Arrange
        _permissions.CannotInvoke.Add(Guest.TryGetMethodMember(SwitchMethod)!);

        // Act
        var rendered = Render<FritzBoxWlanWidget>(parameters => parameters.Add(widget => widget.Subject, Guest));

        // Assert
        var input = FindSwitch(rendered);
        Assert.True(input.HasAttribute("disabled"), rendered.Markup);
        Assert.NotNull(rendered.Find($"[title='{DeniedTooltip}']"));
    }

    [Fact]
    public async Task WhenTheRolesChangeAfterTheSwitchRendered_ThenTheGuestWlanIsNotSwitchedAndTheRefusalIsShown()
    {
        // Arrange
        var wasOn = Guest.IsOn!.Value;
        var rendered = Render<FritzBoxWlanWidget>(parameters => parameters.Add(widget => widget.Subject, Guest));
        _permissions.CannotInvoke.Add(Guest.TryGetMethodMember(SwitchMethod)!);

        // Act
        await FindSwitch(rendered).ChangeAsync(new() { Value = !wasOn });

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => _shownMessages.Count > 0, message: "The refusal should be shown.");
        Assert.Equal("Not allowed", Assert.Single(_shownMessages).Title);
        Assert.DoesNotContain("SetEnable", _fake!.Actions);
        Assert.Equal(wasOn, _fake.IsGuestWlanEnabled);
    }

    [Fact]
    public void WhenTheRolesChange_ThenTheSwitchRendersTheNewAnswer()
    {
        // Arrange
        var rendered = Render<FritzBoxWlanWidget>(parameters => parameters.Add(widget => widget.Subject, Guest));
        Assert.False(FindSwitch(rendered).HasAttribute("disabled"), rendered.Markup);

        // Act
        _permissions.CannotInvoke.Add(Guest.TryGetMethodMember(SwitchMethod)!);
        _permissions.RaiseChanged();

        // Assert
        rendered.WaitForAssertion(() => Assert.True(FindSwitch(rendered).HasAttribute("disabled"), rendered.Markup));
    }

    [Fact]
    public async Task WhenTheWidgetIsDisposed_ThenItNoLongerListensForChanges()
    {
        // Arrange
        Render<FritzBoxWlanWidget>(parameters => parameters.Add(widget => widget.Subject, Guest));
        Assert.Equal(1, _permissions.ChangedSubscriberCount);

        // Act
        await DisposeComponentsAsync();

        // Assert
        Assert.Equal(0, _permissions.ChangedSubscriberCount);
    }

    private static AngleSharp.Dom.IElement FindSwitch(IRenderedComponent<FritzBoxWlanWidget> rendered) =>
        rendered.Find("input[type=checkbox]");
}
